﻿using Notio.Common.IMemory;
using System;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Notio.Network.IO;

/// <summary>
/// Lớp đọc dữ liệu từ socket và xử lý các sự kiện liên quan.
/// </summary>
public class SocketReader : IDisposable
{
    private readonly Socket _socket;
    private readonly IBufferAllocator _multiSizeBuffer;
    private readonly SocketAsyncEventArgs _receiveEventArgs;

    private byte[] _buffer;
    private bool _disposed = false;
    private bool _isReceiving = false;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Sự kiện được kích hoạt khi dữ liệu được nhận.
    /// </summary>
    public event EventHandler<byte[]>? DataReceived;

    /// <summary>
    /// Sự kiện được kích hoạt khi có lỗi.
    /// </summary>
    public event Action<string, Exception>? OnError;

    /// <summary>
    /// Trạng thái bị hủy.
    /// </summary>
    public bool Disposed => _disposed;

    /// <summary>
    /// Trạng thái đang nhận dữ liệu.
    /// </summary>
    public bool IsReceiving => _isReceiving;

    /// <summary>
    /// Khởi tạo đối tượng SocketReader.
    /// </summary>
    /// <param name="socket">Socket để đọc dữ liệu.</param>
    /// <param name="multiSizeBuffer">Bộ nhớ đệm sử dụng nhiều kích thước.</param>
    public SocketReader(Socket socket, IBufferAllocator multiSizeBuffer)
    {
        _cts = new CancellationTokenSource();
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _multiSizeBuffer = multiSizeBuffer ?? throw new ArgumentNullException(nameof(multiSizeBuffer));

        _buffer = _multiSizeBuffer.RentBuffer(256);
        _receiveEventArgs = new SocketAsyncEventArgs();
        _receiveEventArgs.SetBuffer(_buffer, 0, _buffer.Length);
        _receiveEventArgs.Completed += this.OnReceiveCompleted!;
    }

    /// <summary>
    /// Bắt đầu nhận dữ liệu.
    /// </summary>
    /// <param name="externalCancellationToken">Mã hủy ngoài (tùy chọn).</param>
    public void Receive(CancellationToken? externalCancellationToken = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_cts == null || _cts.IsCancellationRequested)
        {
            this.HandleException(new OperationCanceledException(), "Receive cancelled or already stopped.");
            return;
        }

        if (externalCancellationToken != null)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCancellationToken.Value);
        }

        _isReceiving = true;
        this.StartReceiving();
    }

    private void StartReceiving()
    {
        if (_disposed) return;

        try
        {
            if (!_socket.ReceiveAsync(_receiveEventArgs))
            {
                this.OnReceiveCompleted(this, _receiveEventArgs);
            }
        }
        catch (ObjectDisposedException ex)
        {
            this.HandleDispose(ex);
        }
        catch (Exception ex)
        {
            this.HandleException(ex, $"Unexpected error: {ex.Message}");
        }
    }

    private async void OnReceiveCompleted(object sender, SocketAsyncEventArgs e)
    {
        try
        {
            if (_disposed) return;

            if (!HandleSocketError(e))
            {
                this.Dispose();
                throw new InvalidOperationException($"Socket error: {e.SocketError}");
            }

            int bytesRead = e.BytesTransferred;
            if (bytesRead > 0)
            {
                ReadOnlySpan<byte> sizeBytes = e.Buffer.AsSpan(0, 4);
                int dataSize = BitConverter.ToInt32(sizeBytes);

                this.ResizeBufferIfNeeded(dataSize);

                if (e.Buffer != null)
                    this.OnDataReceived(new SocketReceivedEventArgs(e.Buffer.Take(bytesRead).ToArray()));
            }
            else
            {
                await Task.Delay(10).ConfigureAwait(false);
                await Task.Yield();
            }

            this.StartReceiving();
        }
        catch (OperationCanceledException)
        {
            _isReceiving = false;
            this.Dispose();
        }
        catch (Exception ex)
        {
            this.HandleException(ex, $"Error in OnReceiveCompleted: {ex.Message}");
        }
    }

    protected virtual void OnDataReceived(byte[] e)
    {
        DataReceived?.Invoke(this, e);
    }

    /// <summary>
    /// Hủy bỏ việc nhận dữ liệu.
    /// </summary>
    public void Cancel()
    {
        if (_disposed) return;

        try
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _isReceiving = false;
        }
        catch (Exception ex)
        {
            this.HandleException(ex, $"Error while cancelling: {ex.Message}");
        }
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        try
        {
            if (disposing)
            {
                if (_receiveEventArgs != null)
                {
                    _receiveEventArgs.Completed -= this.OnReceiveCompleted!;
                }

                if (_buffer != null)
                {
                    _multiSizeBuffer.ReturnBuffer(_buffer);
                    _buffer = [];
                }

                _cts?.Cancel();
                _cts?.Dispose();

                try
                {
                    _socket.Shutdown(SocketShutdown.Both);
                }
                catch { }

                _socket?.Close(timeout: 1000);
            }
        }
        finally
        {
            _disposed = true;
            _isReceiving = false;
        }
    }

    /// <summary>
    /// Giải phóng tài nguyên và hủy đối tượng.
    /// </summary>
    public void Dispose()
    {
        this.Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    private void ResizeBufferIfNeeded(int dataSize)
    {
        if (dataSize > _buffer.Length)
        {
            _multiSizeBuffer.ReturnBuffer(_buffer);
            _buffer = _multiSizeBuffer.RentBuffer(dataSize);
            _receiveEventArgs.SetBuffer(_buffer, 0, _buffer.Length);
        }
    }

    private void HandleException(Exception ex, string message)
    {
        this.Dispose();
        OnError?.Invoke(message, ex);
    }

    private void HandleDispose(Exception ex)
    {
        _isReceiving = false;
        this.Dispose();
        this.HandleException(ex, $"Socket disposed: {ex.Message}");
    }

    private readonly Func<SocketAsyncEventArgs, bool> HandleSocketError = (e) =>
    {
        return e.SocketError == SocketError.Success;
    };
}