// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net.Http;
using System.Net.Http.QPack;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http3.QPack;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Infrastructure;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Infrastructure.PipeWriterHelpers;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http3
{
    internal class Http3FrameWriter
    {
        private readonly object _writeLock = new object();
        private readonly QPackEncoder _qpackEncoder = new QPackEncoder();
        private readonly Http3Connection _connection;
        private readonly Http3Stream _stream;
        private readonly PipeWriter _outputWriter;
        private readonly ConnectionContext _connectionContext;
        private readonly ITimeoutControl _timeoutControl;
        private readonly MinDataRate _minResponseDataRate;
        private readonly MemoryPool<byte> _memoryPool;
        private readonly IKestrelTrace _log;
        private readonly Http3RawFrame _outgoingFrame;
        private readonly TimingPipeFlusher _flusher;

        // TODO update max frame size
        private uint _maxFrameSize = 10000; //Http3PeerSettings.MinAllowedMaxFrameSize;
        private byte[] _headerEncodingBuffer;

        private long _unflushedBytes;
        private bool _completed;
        private bool _aborted;

        //private int _unflushedBytes;

        public Http3FrameWriter(Http3Connection connection, Http3Stream stream, PipeWriter output, ConnectionContext connectionContext, ITimeoutControl timeoutControl, MinDataRate minResponseDataRate, string connectionId, MemoryPool<byte> memoryPool, IKestrelTrace log)
        {
            _connection = connection;
            _stream = stream;
            _outputWriter = output;
            _connectionContext = connectionContext;
            _timeoutControl = timeoutControl;
            _minResponseDataRate = minResponseDataRate;
            _memoryPool = memoryPool;
            _log = log;
            _outgoingFrame = new Http3RawFrame();
            _flusher = new TimingPipeFlusher(_outputWriter, timeoutControl, log);
            _headerEncodingBuffer = new byte[_maxFrameSize];
        }

        public void UpdateMaxFrameSize(uint maxFrameSize)
        {
            lock (_writeLock)
            {
                if (_maxFrameSize != maxFrameSize)
                {
                    _maxFrameSize = maxFrameSize;
                    _headerEncodingBuffer = new byte[_maxFrameSize];
                }
            }
        }

        internal Task WriteSettingsAsync(IList<Http3PeerSetting> settings)
        {
            _outgoingFrame.PrepareSettings();

            var settingsLength = GetSettingsLength(settings);
            _outgoingFrame.Length = settingsLength;
            WriteHeader(_outgoingFrame, _outputWriter);

            var settingsSpan = _outputWriter.GetSpan((int)settingsLength);

            WriteSettings(settings, settingsSpan);

            _outputWriter.Advance((int)settingsLength);

            return _outputWriter.FlushAsync().AsTask();
        }

        internal static long GetSettingsLength(IList<Http3PeerSetting> settings)
        {
            var totalLength = 0;
            foreach (var setting in settings)
            {
                totalLength += VariableLengthIntegerHelper.GetByteCount((long)setting.Parameter);
                totalLength += VariableLengthIntegerHelper.GetByteCount(setting.Value);
            }

            return totalLength;
        }

        internal static void WriteSettings(IList<Http3PeerSetting> settings, Span<byte> destination)
        {
            foreach (var setting in settings)
            {
                var length = VariableLengthIntegerHelper.WriteInteger(destination, (long)setting.Parameter);
                length += VariableLengthIntegerHelper.WriteInteger(destination.Slice(length), (long)setting.Value);
                destination = destination.Slice(length);
            }
        }

        internal Task WriteStreamIdAsync(long id)
        {
            var buffer = _outputWriter.GetSpan(8);
            _outputWriter.Advance(VariableLengthIntegerHelper.WriteInteger(buffer, id));
            return _outputWriter.FlushAsync().AsTask();
        }

        public ValueTask<FlushResult> WriteDataAsync(in ReadOnlySequence<byte> data)
        {
            // The Length property of a ReadOnlySequence can be expensive, so we cache the value.
            var dataLength = data.Length;

            lock (_writeLock)
            {
                if (_completed)
                {
                    return default;
                }

                WriteDataUnsynchronized(data, dataLength);
                return TimeFlushUnsynchronizedAsync();
            }
        }

        private void WriteDataUnsynchronized(in ReadOnlySequence<byte> data, long dataLength)
        {
            Debug.Assert(dataLength == data.Length);

            _outgoingFrame.PrepareData();

            if (dataLength > _maxFrameSize)
            {
                SplitAndWriteDataUnsynchronized(in data, dataLength);
                return;
            }

            _outgoingFrame.Length = (int)dataLength;

            WriteHeaderUnsynchronized();

            foreach (var buffer in data)
            {
                _outputWriter.Write(buffer.Span);
            }

            return;

            void SplitAndWriteDataUnsynchronized(in ReadOnlySequence<byte> data, long dataLength)
            {
                Debug.Assert(dataLength == data.Length);

                var dataPayloadLength = (int)_maxFrameSize;

                Debug.Assert(dataLength > dataPayloadLength);

                var remainingData = data;
                do
                {
                    var currentData = remainingData.Slice(0, dataPayloadLength);
                    _outgoingFrame.Length = dataPayloadLength;

                    WriteHeaderUnsynchronized();

                    foreach (var buffer in currentData)
                    {
                        _outputWriter.Write(buffer.Span);
                    }

                    dataLength -= dataPayloadLength;
                    remainingData = remainingData.Slice(dataPayloadLength);

                } while (dataLength > dataPayloadLength);

                _outgoingFrame.Length = (int)dataLength;

                WriteHeaderUnsynchronized();

                foreach (var buffer in remainingData)
                {
                    _outputWriter.Write(buffer.Span);
                }
            }
        }

        internal Task WriteGoAway(long id)
        {
            _outgoingFrame.PrepareGoAway();
            var buffer = _outputWriter.GetSpan(9);
            buffer[0] = (byte)_outgoingFrame.Type;

            var length = VariableLengthIntegerHelper.WriteInteger(buffer.Slice(1), id);

            _outgoingFrame.Length = length;

            WriteHeaderUnsynchronized();

            return _outputWriter.FlushAsync().AsTask();
        }

        private void WriteHeaderUnsynchronized()
        {
            var headerLength = WriteHeader(_outgoingFrame, _outputWriter);

            // We assume the payload will be written prior to the next flush.
            _unflushedBytes += headerLength + _outgoingFrame.Length;
        }

        internal static int WriteHeader(Http3RawFrame frame, PipeWriter output)
        {
            // max size of the header is 16, most likely it will be smaller.
            var buffer = output.GetSpan(16);

            var typeLength = VariableLengthIntegerHelper.WriteInteger(buffer, (int)frame.Type);

            buffer = buffer.Slice(typeLength);

            var lengthLength = VariableLengthIntegerHelper.WriteInteger(buffer, (int)frame.Length);

            var totalLength = typeLength + lengthLength;
            output.Advance(typeLength + lengthLength);

            return totalLength;
        }

        public ValueTask<FlushResult> WriteResponseTrailers(HttpResponseTrailers headers)
        {
            lock (_writeLock)
            {
                if (_completed)
                {
                    return default;
                }

                try
                {
                    _outgoingFrame.PrepareHeaders();
                    var buffer = _headerEncodingBuffer.AsSpan();
                    var done = _qpackEncoder.BeginEncode(EnumerateHeaders(headers), buffer, out var payloadLength);
                    FinishWritingHeaders(payloadLength, done);
                }
                catch (QPackEncodingException)
                {
                    //_log.HPackEncodingError(_connectionId, streamId, hex);
                    //_http3Stream.Abort(new ConnectionAbortedException(hex.Message, hex));
                }

                return TimeFlushUnsynchronizedAsync();
            }
        }

        private ValueTask<FlushResult> TimeFlushUnsynchronizedAsync()
        {
            var bytesWritten = _unflushedBytes;
            _unflushedBytes = 0;

            return _flusher.FlushAsync(_minResponseDataRate, bytesWritten);
        }

        public ValueTask<FlushResult> FlushAsync(IHttpOutputAborter outputAborter, CancellationToken cancellationToken)
        {
            lock (_writeLock)
            {
                if (_completed)
                {
                    return default;
                }

                var bytesWritten = _unflushedBytes;
                _unflushedBytes = 0;

                return _flusher.FlushAsync(_minResponseDataRate, bytesWritten, outputAborter, cancellationToken);
            }
        }

        internal void WriteResponseHeaders(int statusCode, IHeaderDictionary headers)
        {
            lock (_writeLock)
            {
                if (_completed)
                {
                    return;
                }

                try
                {

                    lock (_connection._sync)
                    {
                        _outgoingFrame.PrepareHeaders();
                        var buffer = _headerEncodingBuffer.AsSpan();
                        _log.LogInformation(_connection.GetMaxHeaderListSize().ToString());
                        _log.LogInformation(CalculateHeaderSize(EnumerateHeaders(headers)).ToString());

                        if (CalculateHeaderSize(EnumerateHeaders(headers)) > _connection.GetMaxHeaderListSize())
                        {
                            _stream.Abort(new ConnectionAbortedException("Exceeded client request max header list size."), Http3ErrorCode.ProtocolError);
                            return;
                        }

                        var done = _qpackEncoder.BeginEncode(statusCode, EnumerateHeaders(headers), buffer, out var payloadLength);

                        // TODO this check is against the compressed byte count rather than uncompressed.
                        // See https://quicwg.org/base-drafts/draft-ietf-quic-http.html#name-header-size-constraints

                        FinishWritingHeaders(payloadLength, done);
                    }
                }
                catch (QPackEncodingException hex)
                {
                    // TODO figure out how to abort the stream here.
                    //_http3Stream.Abort(new ConnectionAbortedException(hex.Message, hex));
                    throw new InvalidOperationException(hex.Message, hex); // Report the error to the user if this was the first write.
                }
            }
        }

        private int CalculateHeaderSize(IEnumerable<KeyValuePair<string, string>> enumerable)
        {
            var size = 0;
            // https://quicwg.org/base-drafts/draft-ietf-quic-http.html#name-header-size-constraints
            // The size of a header field list is calculated based on the uncompressed size of header fields,
            // including the length of the name and value in bytes plus an overhead of 32 bytes for each header field.
            foreach (var header in enumerable)
            {
                size += header.Key.Length + header.Value.Length + 32;
            }

            return size;
        }

        private void FinishWritingHeaders(int payloadLength, bool done)
        {
            var buffer = _headerEncodingBuffer.AsSpan();
            _outgoingFrame.Length = payloadLength;

            WriteHeaderUnsynchronized();
            _outputWriter.Write(buffer.Slice(0, payloadLength));

            while (!done)
            {
                done = _qpackEncoder.Encode(buffer, out payloadLength);
                _outgoingFrame.Length = payloadLength;

                WriteHeaderUnsynchronized();
                _outputWriter.Write(buffer.Slice(0, payloadLength));
            }
        }

        public void Complete()
        {
            lock (_writeLock)
            {
                if (_completed)
                {
                    return;
                }

                _completed = true;
                _outputWriter.Complete();
            }
        }

        public void Abort(ConnectionAbortedException error)
        {
            lock (_writeLock)
            {
                if (_aborted)
                {
                    return;
                }

                _aborted = true;
                _connectionContext.Abort(error);

                Complete();
            }
        }

        private static IEnumerable<KeyValuePair<string, string>> EnumerateHeaders(IHeaderDictionary headers)
        {
            foreach (var header in headers)
            {
                foreach (var value in header.Value)
                {
                    yield return new KeyValuePair<string, string>(header.Key, value);
                }
            }
        }
    }
}