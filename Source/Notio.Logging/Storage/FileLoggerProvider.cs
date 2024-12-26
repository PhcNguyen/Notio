﻿using Notio.Logging.Base;
using Notio.Logging.Metadata;
using Notio.Logging.Sinks;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace Notio.Logging.Storage;

/// <summary>
/// Nhà cung cấp bộ ghi log tệp tin tổng quát.
/// </summary>
public class LoggerProvider
{
    private readonly ConcurrentDictionary<string, FileLogSinks> loggers = new();
    private readonly BlockingCollection<string> entryQueue = new(1024);
    private readonly Task processQueueTask;
    private readonly FileWriter fWriter;

    internal LoggerOptions Options { get; private set; }

    public string LogFileName;
    public bool Append => Options.Append;
    public long FileSizeLimitBytes => Options.FileSizeLimitBytes;
    public int MaxRollingFiles => Options.MaxRollingFiles;

    /// <summary>
    /// Sử dụng múi giờ UTC cho dấu thời gian trong các thông báo log hay không. Mặc định là false.
    /// </summary>
    public bool UseUtcTimestamp
    {
        get => Options.UseUtcTimestamp;
        set { Options.UseUtcTimestamp = value; }
    }

    /// <summary>
    /// Bộ định dạng tùy chỉnh cho dòng nhập log.
    /// </summary>
    public Func<LogMessage, string> FormatLogEntry
    {
        get => Options.FormatLogEntry;
        set { Options.FormatLogEntry = value; }
    }

    /// <summary>
    /// Bộ định dạng tùy chỉnh cho tên tệp log.
    /// </summary>
    public Func<string, string> FormatLogFileName
    {
        get => Options.FormatLogFileName;
        set { Options.FormatLogFileName = value; }
    }

    /// <summary>
    /// Bộ xử lý tùy chỉnh cho lỗi tệp.
    /// </summary>
    public Action<FileError> HandleFileError
    {
        get => Options.HandleFileError;
        set { Options.HandleFileError = value; }
    }

    /// <summary>
    /// Khởi tạo <see cref="LoggerProvider"/> với tên tệp tin và tùy chọn ghi đè mặc định.
    /// </summary>
    /// <param name="fileName">Tên tệp tin.</param>
    public LoggerProvider(string fileName) : this(fileName, true)
    {
    }

    /// <summary>
    /// Khởi tạo <see cref="LoggerProvider"/> với tên tệp tin và tùy chọn ghi đè.
    /// </summary>
    /// <param name="fileName">Tên tệp tin.</param>
    /// <param name="append">Tùy chọn ghi đè.</param>
    public LoggerProvider(string fileName, bool append) : this(fileName, new LoggerOptions() { Append = append })
    {
    }

    /// <summary>
    /// Khởi tạo <see cref="LoggerProvider"/> với tên tệp tin và tùy chọn cấu hình.
    /// </summary>
    /// <param name="fileName">Tên tệp tin.</param>
    /// <param name="options">Tùy chọn cấu hình.</param>
    public LoggerProvider(string fileName, LoggerOptions options)
    {
        Options = options;
        LogFileName = Environment.ExpandEnvironmentVariables(fileName);

        fWriter = new FileWriter(this);
        processQueueTask = Task.Factory.StartNew(
            ProcessQueue,
            this,
            TaskCreationOptions.LongRunning);
    }

    /// <summary>
    /// Giải phóng tài nguyên.
    /// </summary>
    public void Dispose()
    {
        entryQueue.CompleteAdding();
        try
        {
            processQueueTask.Wait(1500);  // giống như trong ConsoleLogger
        }
        catch (TaskCanceledException)
        {
        }
        catch (AggregateException ex) when (ex.InnerExceptions.Count == 1 && ex.InnerExceptions[0] is TaskCanceledException) { }

        loggers.Clear();
        fWriter.Close();
    }

    internal void WriteEntry(string message)
    {
        if (!entryQueue.IsAddingCompleted)
        {
            try
            {
                entryQueue.Add(message);
                return;
            }
            catch (InvalidOperationException) { }
        }
        // không làm gì
    }

    private void ProcessQueue()
    {
        var writeMessageFailed = false;
        foreach (var message in entryQueue.GetConsumingEnumerable())
        {
            try
            {
                if (!writeMessageFailed)
                    fWriter.WriteMessage(message, entryQueue.Count == 0);
            }
            catch (Exception ex)
            {
                // có lỗi xảy ra. Mã ứng dụng có thể xử lý nếu 'HandleFileError' được cung cấp
                var stopLogging = true;
                if (HandleFileError != null)
                {
                    var fileErr = new FileError(LogFileName, ex);
                    try
                    {
                        HandleFileError(fileErr);
                        if (fileErr.NewLogFileName != null)
                        {
                            fWriter.UseNewLogFile(fileErr.NewLogFileName);
                            // ghi lại thông báo thất bại vào tệp log mới
                            fWriter.WriteMessage(message, entryQueue.Count == 0);
                            stopLogging = false;
                        }
                    }
                    catch
                    {
                        // có thể xảy ra ngoại lệ trong HandleFileError hoặc tên tệp đề xuất không thể sử dụng
                        // bỏ qua trong trường hợp đó -> bộ ghi log sẽ ngừng xử lý thông báo log
                    }
                }
                if (stopLogging)
                {
                    // Ngừng xử lý thông báo log do không thể ghi vào tệp log
                    entryQueue.CompleteAdding();
                    writeMessageFailed = true;
                }
            }
        }
    }

    private static void ProcessQueue(object state)
    {
        var fileLogger = (LoggerProvider)state;
        fileLogger.ProcessQueue();
    }
}