// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Microsoft.Net.Http.Server
{
    internal class RequestStream : Stream
    {
        private const int MaxReadSize = 0x20000; // http.sys recommends we limit reads to 128k

        private RequestContext _requestContext;
        private uint _dataChunkOffset;
        private int _dataChunkIndex;
        private bool _closed;

        internal RequestStream(RequestContext httpContext)
        {
            _requestContext = httpContext;
        }

        internal RequestContext RequestContext
        {
            get { return _requestContext; }
        }

        private SafeHandle RequestQueueHandle => RequestContext.Server.RequestQueue.Handle;

        private ulong RequestId => RequestContext.Request.RequestId;

        private ILogger Logger => RequestContext.Server.Logger;

        public override bool CanSeek
        {
            get
            {
                return false;
            }
        }

        public override bool CanWrite
        {
            get
            {
                return false;
            }
        }

        public override bool CanRead
        {
            get
            {
                return true;
            }
        }

        public override long Length
        {
            get
            {
                throw new NotSupportedException(Resources.Exception_NoSeek);
            }
        }

        public override long Position
        {
            get
            {
                throw new NotSupportedException(Resources.Exception_NoSeek);
            }
            set
            {
                throw new NotSupportedException(Resources.Exception_NoSeek);
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException(Resources.Exception_NoSeek);
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException(Resources.Exception_NoSeek);
        }

        public override void Flush()
        {
            throw new InvalidOperationException(Resources.Exception_ReadOnlyStream);
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            throw new InvalidOperationException(Resources.Exception_ReadOnlyStream);
        }

        internal void Abort()
        {
            _closed = true;
            _requestContext.Abort();
        }

        private void ValidateReadBuffer(byte[] buffer, int offset, int size)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException("buffer");
            }
            if (offset < 0 || offset > buffer.Length)
            {
                throw new ArgumentOutOfRangeException("offset", offset, string.Empty);
            }
            if (size <= 0 || size > buffer.Length - offset)
            {
                throw new ArgumentOutOfRangeException("size", size, string.Empty);
            }
        }

        public override unsafe int Read([In, Out] byte[] buffer, int offset, int size)
        {
            ValidateReadBuffer(buffer, offset, size);
            if (_closed)
            {
                return 0;
            }
            // TODO: Verbose log parameters

            uint dataRead = 0;

            if (_dataChunkIndex != -1)
            {
                dataRead = _requestContext.Request.GetChunks(ref _dataChunkIndex, ref _dataChunkOffset, buffer, offset, size);
            }

            if (_dataChunkIndex == -1 && dataRead < size)
            {
                uint statusCode = 0;
                uint extraDataRead = 0;
                offset += (int)dataRead;
                size -= (int)dataRead;

                // the http.sys team recommends that we limit the size to 128kb
                if (size > MaxReadSize)
                {
                    size = MaxReadSize;
                }

                fixed (byte* pBuffer = buffer)
                {
                    // issue unmanaged blocking call

                    uint flags = 0;

                    statusCode =
                        HttpApi.HttpReceiveRequestEntityBody(
                            RequestQueueHandle,
                            RequestId,
                            flags,
                            (IntPtr)(pBuffer + offset),
                            (uint)size,
                            out extraDataRead,
                            SafeNativeOverlapped.Zero);

                    dataRead += extraDataRead;
                }
                if (statusCode != UnsafeNclNativeMethods.ErrorCodes.ERROR_SUCCESS && statusCode != UnsafeNclNativeMethods.ErrorCodes.ERROR_HANDLE_EOF)
                {
                    Exception exception = new IOException(string.Empty, new WebListenerException((int)statusCode));
                    LogHelper.LogException(Logger, "Read", exception);
                    Abort();
                    throw exception;
                }
                UpdateAfterRead(statusCode, dataRead);
            }

            // TODO: Verbose log dump data read
            return (int)dataRead;
        }

        internal void UpdateAfterRead(uint statusCode, uint dataRead)
        {
            if (statusCode == UnsafeNclNativeMethods.ErrorCodes.ERROR_HANDLE_EOF || dataRead == 0)
            {
                Dispose();
            }
        }

#if NETSTANDARD1_3
        public unsafe IAsyncResult BeginRead(byte[] buffer, int offset, int size, AsyncCallback callback, object state)
#else
        public override unsafe IAsyncResult BeginRead(byte[] buffer, int offset, int size, AsyncCallback callback, object state)
#endif
        {
            ValidateReadBuffer(buffer, offset, size);
            if (_closed)
            {
                RequestStreamAsyncResult result = new RequestStreamAsyncResult(this, state, callback);
                result.Complete(0);
                return result;
            }
            // TODO: Verbose log parameters

            RequestStreamAsyncResult asyncResult = null;

            uint dataRead = 0;
            if (_dataChunkIndex != -1)
            {
                dataRead = _requestContext.Request.GetChunks(ref _dataChunkIndex, ref _dataChunkOffset, buffer, offset, size);

                if (_dataChunkIndex != -1 && dataRead == size)
                {
                    asyncResult = new RequestStreamAsyncResult(this, state, callback, buffer, offset, 0);
                    asyncResult.Complete((int)dataRead);
                }
            }

            if (_dataChunkIndex == -1 && dataRead < size)
            {
                uint statusCode = 0;
                offset += (int)dataRead;
                size -= (int)dataRead;

                // the http.sys team recommends that we limit the size to 128kb
                if (size > MaxReadSize)
                {
                    size = MaxReadSize;
                }

                asyncResult = new RequestStreamAsyncResult(this, state, callback, buffer, offset, dataRead);
                uint bytesReturned;

                try
                {
                    uint flags = 0;

                    statusCode =
                        HttpApi.HttpReceiveRequestEntityBody(
                            RequestQueueHandle,
                            RequestId,
                            flags,
                            asyncResult.PinnedBuffer,
                            (uint)size,
                            out bytesReturned,
                            asyncResult.NativeOverlapped);
                }
                catch (Exception e)
                {
                    LogHelper.LogException(Logger, "BeginRead", e);
                    asyncResult.Dispose();
                    throw;
                }

                if (statusCode != UnsafeNclNativeMethods.ErrorCodes.ERROR_SUCCESS && statusCode != UnsafeNclNativeMethods.ErrorCodes.ERROR_IO_PENDING)
                {
                    asyncResult.Dispose();
                    if (statusCode == UnsafeNclNativeMethods.ErrorCodes.ERROR_HANDLE_EOF)
                    {
                        asyncResult = new RequestStreamAsyncResult(this, state, callback, dataRead);
                        asyncResult.Complete((int)bytesReturned);
                    }
                    else
                    {
                        Exception exception = new IOException(string.Empty, new WebListenerException((int)statusCode));
                        LogHelper.LogException(Logger, "BeginRead", exception);
                        Abort();
                        throw exception;
                    }
                }
                else if (statusCode == UnsafeNclNativeMethods.ErrorCodes.ERROR_SUCCESS &&
                         WebListener.SkipIOCPCallbackOnSuccess)
                {
                    // IO operation completed synchronously - callback won't be called to signal completion.
                    asyncResult.IOCompleted(statusCode, bytesReturned);
                }
            }
            return asyncResult;
        }

#if NETSTANDARD1_3
        public int EndRead(IAsyncResult asyncResult)
#else
        public override int EndRead(IAsyncResult asyncResult)
#endif
        {
            if (asyncResult == null)
            {
                throw new ArgumentNullException("asyncResult");
            }
            RequestStreamAsyncResult castedAsyncResult = asyncResult as RequestStreamAsyncResult;
            if (castedAsyncResult == null || castedAsyncResult.RequestStream != this)
            {
                throw new ArgumentException(Resources.Exception_WrongIAsyncResult, "asyncResult");
            }
            if (castedAsyncResult.EndCalled)
            {
                throw new InvalidOperationException(Resources.Exception_EndCalledMultipleTimes);
            }
            castedAsyncResult.EndCalled = true;
            // wait & then check for errors
            // Throws on failure
            int dataRead = castedAsyncResult.Task.Result;
            // TODO: Verbose log #dataRead.
            return dataRead;
        }

        public override unsafe Task<int> ReadAsync(byte[] buffer, int offset, int size, CancellationToken cancellationToken)
        {
            ValidateReadBuffer(buffer, offset, size);
            if (_closed)
            {
                return Task.FromResult<int>(0);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Helpers.CanceledTask<int>();
            }
            // TODO: Verbose log parameters

            RequestStreamAsyncResult asyncResult = null;

            uint dataRead = 0;
            if (_dataChunkIndex != -1)
            {
                dataRead = _requestContext.Request.GetChunks(ref _dataChunkIndex, ref _dataChunkOffset, buffer, offset, size);
                if (_dataChunkIndex != -1 && dataRead == size)
                {
                    UpdateAfterRead(UnsafeNclNativeMethods.ErrorCodes.ERROR_SUCCESS, dataRead);
                    // TODO: Verbose log #dataRead
                    return Task.FromResult<int>((int)dataRead);
                }
            }

            if (_dataChunkIndex == -1 && dataRead < size)
            {
                uint statusCode = 0;
                offset += (int)dataRead;
                size -= (int)dataRead;

                // the http.sys team recommends that we limit the size to 128kb
                if (size > MaxReadSize)
                {
                    size = MaxReadSize;
                }

                var cancellationRegistration = default(CancellationTokenRegistration);
                if (cancellationToken.CanBeCanceled)
                {
                    cancellationRegistration = RequestContext.RegisterForCancellation(cancellationToken);
                }

                asyncResult = new RequestStreamAsyncResult(this, null, null, buffer, offset, dataRead, cancellationRegistration);
                uint bytesReturned;

                try
                {
                    uint flags = 0;

                    statusCode =
                        HttpApi.HttpReceiveRequestEntityBody(
                            RequestQueueHandle,
                            RequestId,
                            flags,
                            asyncResult.PinnedBuffer,
                            (uint)size,
                            out bytesReturned,
                            asyncResult.NativeOverlapped);
                }
                catch (Exception e)
                {
                    asyncResult.Dispose();
                    Abort();
                    LogHelper.LogException(Logger, "ReadAsync", e);
                    throw;
                }

                if (statusCode != UnsafeNclNativeMethods.ErrorCodes.ERROR_SUCCESS && statusCode != UnsafeNclNativeMethods.ErrorCodes.ERROR_IO_PENDING)
                {
                    asyncResult.Dispose();
                    if (statusCode == UnsafeNclNativeMethods.ErrorCodes.ERROR_HANDLE_EOF)
                    {
                        uint totalRead = dataRead + bytesReturned;
                        UpdateAfterRead(statusCode, totalRead);
                        // TODO: Verbose log totalRead
                        return Task.FromResult<int>((int)totalRead);
                    }
                    else
                    {
                        Exception exception = new IOException(string.Empty, new WebListenerException((int)statusCode));
                        LogHelper.LogException(Logger, "ReadAsync", exception);
                        Abort();
                        throw exception;
                    }
                }
                else if (statusCode == UnsafeNclNativeMethods.ErrorCodes.ERROR_SUCCESS &&
                         WebListener.SkipIOCPCallbackOnSuccess)
                {
                    // IO operation completed synchronously - callback won't be called to signal completion.
                    asyncResult.Dispose();
                    uint totalRead = dataRead + bytesReturned;
                    UpdateAfterRead(statusCode, totalRead);
                    // TODO: Verbose log
                    return Task.FromResult<int>((int)totalRead);
                }
            }
            return asyncResult.Task;
        }

        public override void Write(byte[] buffer, int offset, int size)
        {
            throw new InvalidOperationException(Resources.Exception_ReadOnlyStream);
        }

#if NETSTANDARD1_3
        public IAsyncResult BeginWrite(byte[] buffer, int offset, int size, AsyncCallback callback, object state)
#else
        public override IAsyncResult BeginWrite(byte[] buffer, int offset, int size, AsyncCallback callback, object state)
#endif
        {
            throw new InvalidOperationException(Resources.Exception_ReadOnlyStream);
        }

#if NETSTANDARD1_3
        public void EndWrite(IAsyncResult asyncResult)
#else
        public override void EndWrite(IAsyncResult asyncResult)
#endif
        {
            throw new InvalidOperationException(Resources.Exception_ReadOnlyStream);
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                _closed = true;
            }
            finally
            {
                base.Dispose(disposing);
            }
        }
    }
}
