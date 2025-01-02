﻿using Notio.Shared.Configuration;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Notio.Logging;

namespace Notio.Network.Firewall
{
    /// <summary>
    /// Lớp quản lý và giới hạn băng thông cho mỗi kết nối
    /// </summary>
    public sealed class BandwidthLimiter : IDisposable
    {
        private readonly record struct BandwidthInfo(
            long BytesSent,
            long BytesReceived,
            DateTime LastResetTime,
            DateTime LastActivityTime
        );

        private readonly record struct DataRateLimit(
            long BytesPerSecond,
            int BurstSize
        );

        private readonly ConcurrentDictionary<string, SemaphoreSlim> _throttles;
        private readonly ConcurrentDictionary<string, BandwidthInfo> _stats;
        private readonly FirewallConfig _networkConfig;
        private readonly DataRateLimit _downloadLimit;
        private readonly DataRateLimit _uploadLimit;
        private readonly TimeSpan _resetInterval;
        private readonly Timer _resetTimer;

        private bool _disposed;

        public BandwidthLimiter(FirewallConfig? networkConfig = null)
        {
            _networkConfig = networkConfig ?? ConfigManager.Instance.GetConfig<FirewallConfig>();

            // Kiểm tra cấu hình
            if (_networkConfig.MaxUploadBytesPerSecond <= 0 || _networkConfig.MaxDownloadBytesPerSecond <= 0)
                throw new ArgumentException("Bandwidth limits must be greater than 0");

            _uploadLimit = new DataRateLimit(
                _networkConfig.MaxUploadBytesPerSecond,
                _networkConfig.UploadBurstSize
            );

            _downloadLimit = new DataRateLimit(
                _networkConfig.MaxDownloadBytesPerSecond,
                _networkConfig.DownloadBurstSize
            );

            _stats = new ConcurrentDictionary<string, BandwidthInfo>();
            _throttles = new ConcurrentDictionary<string, SemaphoreSlim>();
            _resetInterval = TimeSpan.FromSeconds(_networkConfig.BandwidthResetIntervalSeconds);

            // Tạo timer để reset số liệu định kỳ
            _resetTimer = new Timer(
                _ => ResetBandwidthStats(),
                null,
                _resetInterval,
                _resetInterval
            );
        }

        /// <summary>
        /// Kiểm tra và ghi nhận dữ liệu gửi đi
        /// </summary>
        public async Task<bool> TryUploadAsync(string endPoint, int byteCount, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(BandwidthLimiter));

            if (string.IsNullOrWhiteSpace(endPoint))
                throw new ArgumentException("EndPoint cannot be null or whitespace", nameof(endPoint));
            if (byteCount <= 0)
                throw new ArgumentException("Byte count must be greater than 0", nameof(byteCount));

            var throttle = _throttles.GetOrAdd(endPoint, _ => new SemaphoreSlim(_uploadLimit.BurstSize));

            try
            {
                if (!await throttle.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken))
                {
                    NotioLog.Instance.Trace($"Upload throttled for IP: {endPoint}");
                    return false;
                }

                var stats = _stats.AddOrUpdate(
                    endPoint,
                    _ => new BandwidthInfo(byteCount, 0, DateTime.UtcNow, DateTime.UtcNow),
                    (_, current) =>
                    {
                        var newTotal = current.BytesSent + byteCount;
                        if (newTotal > _uploadLimit.BytesPerSecond)
                        {
                            NotioLog.Instance.Trace($"Upload limit exceeded for IP: {endPoint}");
                            return current;
                        }
                        return current with
                        {
                            BytesSent = newTotal,
                            LastActivityTime = DateTime.UtcNow
                        };
                    }
                );

                return stats.BytesSent <= _uploadLimit.BytesPerSecond;
            }
            finally
            {
                throttle.Release();
            }
        }

        /// <summary>
        /// Kiểm tra và ghi nhận dữ liệu nhận về
        /// </summary>
        public async Task<bool> TryDownloadAsync(string endPoint, int byteCount, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(BandwidthLimiter));

            if (string.IsNullOrWhiteSpace(endPoint))
                throw new ArgumentException("EndPoint cannot be null or whitespace", nameof(endPoint));
            if (byteCount <= 0)
                throw new ArgumentException("Byte count must be greater than 0", nameof(byteCount));

            var throttle = _throttles.GetOrAdd(endPoint, _ => new SemaphoreSlim(_downloadLimit.BurstSize));

            try
            {
                if (!await throttle.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken))
                {
                    NotioLog.Instance.Trace($"Download throttled for IP: {endPoint}");
                    return false;
                }

                var stats = _stats.AddOrUpdate(
                    endPoint,
                    _ => new BandwidthInfo(0, byteCount, DateTime.UtcNow, DateTime.UtcNow),
                    (_, current) =>
                    {
                        var newTotal = current.BytesReceived + byteCount;
                        if (newTotal > _downloadLimit.BytesPerSecond)
                        {
                            NotioLog.Instance.Trace($"Download limit exceeded for IP: {endPoint}");
                            return current;
                        }
                        return current with
                        {
                            BytesReceived = newTotal,
                            LastActivityTime = DateTime.UtcNow
                        };
                    }
                );

                return stats.BytesReceived <= _downloadLimit.BytesPerSecond;
            }
            finally
            {
                throttle.Release();
            }
        }

        /// <summary>
        /// Lấy thông tin băng thông hiện tại của một IP
        /// </summary>
        public (long BytesSent, long BytesReceived, DateTime LastActivity) GetBandwidthInfo(string endPoint)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(BandwidthLimiter));

            if (string.IsNullOrWhiteSpace(endPoint))
                throw new ArgumentException("EndPoint cannot be null or whitespace", nameof(endPoint));

            var stats = _stats.GetValueOrDefault(endPoint);
            return (stats.BytesSent, stats.BytesReceived, stats.LastActivityTime);
        }

        /// <summary>
        /// Reset số liệu thống kê băng thông định kỳ
        /// </summary>
        private void ResetBandwidthStats()
        {
            if (_disposed) return;

            var now = DateTime.UtcNow;
            foreach (var kvp in _stats)
            {
                if (now - kvp.Value.LastResetTime >= _resetInterval)
                {
                    _stats.TryUpdate(
                        kvp.Key,
                        kvp.Value with
                        {
                            BytesSent = 0,
                            BytesReceived = 0,
                            LastResetTime = now
                        },
                        kvp.Value
                    );
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            _resetTimer.Dispose();
            foreach (var throttle in _throttles.Values)
            {
                throttle.Dispose();
            }
            _throttles.Clear();
            _stats.Clear();
        }
    }
}