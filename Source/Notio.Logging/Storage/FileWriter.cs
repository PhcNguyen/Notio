﻿using Notio.Logging.Base;
using System;
using System.IO;
using System.Linq;

namespace Notio.Logging.Storage;

/// <summary>
/// Lớp quản lý ghi log vào tệp tin.
/// </summary>
internal class FileWriter
{
    private readonly LoggerProvider FileLogPrv;
    private string LogFileName;
    private int RollingNumber;
    private FileStream LogFileStream;
    private StreamWriter LogFileWriter;

    internal FileWriter(LoggerProvider fileLogPrv)
    {
        FileLogPrv = fileLogPrv;

        DetermineLastFileLogName();
        OpenFile(FileLogPrv.Append);
    }

    private string GetBaseLogFileName()
    {
        var fName = FileLogPrv.LogFileName;
        if (FileLogPrv.FormatLogFileName != null)
            fName = FileLogPrv.FormatLogFileName(fName);
        return fName;
    }

    private void DetermineLastFileLogName()
    {
        var baseLogFileName = GetBaseLogFileName();
        __LastBaseLogFileName = baseLogFileName;
        if (FileLogPrv.FileSizeLimitBytes > 0)
        {
            // rolling file is used
            if (FileLogPrv.Options.RollingFilesConvention == LoggerOptions.FileRollingConvention.Ascending)
            {
                var logFiles = GetExistingLogFiles(baseLogFileName);
                if (logFiles.Length > 0)
                {
                    var lastFileInfo = logFiles
                            .OrderByDescending(fInfo => fInfo.Name)
                            .OrderByDescending(fInfo => fInfo.LastWriteTime).First();
                    LogFileName = lastFileInfo.FullName;
                }
                else
                {
                    // no files yet, use default name
                    LogFileName = baseLogFileName;
                }
            }
            else
            {
                LogFileName = baseLogFileName;
            }
        }
        else
        {
            LogFileName = baseLogFileName;
        }
    }

    private void CreateLogFileStream(bool append)
    {
        var fileInfo = new FileInfo(LogFileName);
        // Directory.Create will check if the directory already exists,
        // so there is no need for a "manual" check first.
        fileInfo.Directory.Create();

        LogFileStream = new FileStream(LogFileName, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        if (append)
        {
            LogFileStream.Seek(0, SeekOrigin.End);
        }
        else
        {
            LogFileStream.SetLength(0); // clear the file
        }
        LogFileWriter = new StreamWriter(LogFileStream);
    }

    internal void UseNewLogFile(string newLogFileName)
    {
        FileLogPrv.LogFileName = newLogFileName;
        DetermineLastFileLogName(); // preserve all existing logic related to 'FormatLogFileName' and rolling files
        CreateLogFileStream(FileLogPrv.Append);  // if file error occurs here it is not handled by 'HandleFileError' recursively
    }

    private void OpenFile(bool append)
    {
        try
        {
            CreateLogFileStream(append);
        }
        catch (Exception ex)
        {
            if (FileLogPrv.HandleFileError != null)
            {
                var fileErr = new FileError(LogFileName, ex);
                FileLogPrv.HandleFileError(fileErr);
                if (fileErr.NewLogFileName != null)
                {
                    UseNewLogFile(fileErr.NewLogFileName);
                }
            }
            else
            {
                throw; // do not handle by default to preserve backward compatibility
            }
        }
    }

    private string GetNextFileLogName()
    {
        var baseLogFileName = GetBaseLogFileName();
        // if file does not exist or file size limit is not reached - do not add rolling file index
        if (!File.Exists(baseLogFileName) ||
            FileLogPrv.FileSizeLimitBytes <= 0 ||
            new FileInfo(baseLogFileName).Length < FileLogPrv.FileSizeLimitBytes)
            return baseLogFileName;

        switch (FileLogPrv.Options.RollingFilesConvention)
        {
            case LoggerOptions.FileRollingConvention.Ascending:
                //Unchanged default handling just optimized for performance and code reuse
                int currentFileIndex = GetIndexFromFile(baseLogFileName, LogFileName);
                var nextFileIndex = currentFileIndex + 1;
                if (FileLogPrv.MaxRollingFiles > 0)
                {
                    nextFileIndex %= FileLogPrv.MaxRollingFiles;
                }
                return GetFileFromIndex(baseLogFileName, nextFileIndex);

            case LoggerOptions.FileRollingConvention.AscendingStableBase:
                {
                    //Move current base file to next rolling file number
                    RollingNumber++;
                    if (FileLogPrv.MaxRollingFiles > 0)
                    {
                        RollingNumber %= FileLogPrv.MaxRollingFiles - 1;
                    }
                    var moveFile = GetFileFromIndex(baseLogFileName, RollingNumber + 1);
                    if (File.Exists(moveFile))
                    {
                        File.Delete(moveFile);
                    }
                    File.Move(baseLogFileName, moveFile);
                    return baseLogFileName;
                }
            case LoggerOptions.FileRollingConvention.Descending:
                {
                    //Move all existing files to index +1 except if they are > MaxRollingFiles
                    var logFiles = GetExistingLogFiles(baseLogFileName);
                    if (logFiles.Length > 0)
                    {
                        foreach (var finfo in logFiles.OrderByDescending(fInfo => fInfo.Name))
                        {
                            var index = GetIndexFromFile(baseLogFileName, finfo.Name);
                            if (FileLogPrv.MaxRollingFiles > 0 && index >= FileLogPrv.MaxRollingFiles - 1)
                            {
                                continue;
                            }
                            var moveFile = GetFileFromIndex(baseLogFileName, index + 1);
                            if (File.Exists(moveFile))
                            {
                                File.Delete(moveFile);
                            }
                            File.Move(finfo.FullName, moveFile);
                        }
                    }
                    return baseLogFileName;
                }
        }
        throw new NotImplementedException("RollingFilesConvention");
    }

    // lưu tên tệp log cơ bản cuối cùng được trả về để tránh kiểm tra quá mức trong CheckForNewLogFile.isBaseFileNameChanged
    private string __LastBaseLogFileName = null;

    private void CheckForNewLogFile()
    {
        bool openNewFile = false;
        if (isMaxFileSizeThresholdReached() || isBaseFileNameChanged())
            openNewFile = true;

        if (openNewFile)
        {
            Close();
            LogFileName = GetNextFileLogName();
            OpenFile(false);
        }

        bool isMaxFileSizeThresholdReached()
        {
            return FileLogPrv.FileSizeLimitBytes > 0 && LogFileStream.Length > FileLogPrv.FileSizeLimitBytes;
        }
        bool isBaseFileNameChanged()
        {
            if (FileLogPrv.FormatLogFileName != null)
            {
                var baseLogFileName = GetBaseLogFileName();
                if (baseLogFileName != __LastBaseLogFileName)
                {
                    __LastBaseLogFileName = baseLogFileName;
                    return true;
                }
                return false;
            }
            return false;
        }
    }

    internal void WriteMessage(string message, bool flush)
    {
        if (LogFileWriter != null)
        {
            CheckForNewLogFile();
            LogFileWriter.WriteLine(message);
            if (flush)
                LogFileWriter.Flush();
        }
    }

    /// <summary>
    /// Returns the index of a file or 0 if none found
    /// </summary>
    private static int GetIndexFromFile(string baseLogFileName, string filename)
    {
        var baseFileNameOnly = Path.GetFileNameWithoutExtension(baseLogFileName.AsSpan());
        var currentFileNameOnly = Path.GetFileNameWithoutExtension(filename.AsSpan());

        var suffix = currentFileNameOnly[baseFileNameOnly.Length..];

        if (suffix.Length > 0 && int.TryParse(suffix, out var parsedIndex))
        {
            return parsedIndex;
        }
        return 0;
    }

    private static string GetFileFromIndex(string baseLogFileName, int index)
    {
        var nextFileName = string.Concat(Path.GetFileNameWithoutExtension(baseLogFileName.AsSpan()), index > 0 ? index.ToString() : "", Path.GetExtension(baseLogFileName.AsSpan()));
        return string.Concat(Path.Join(Path.GetDirectoryName(baseLogFileName.AsSpan()), nextFileName.AsSpan()));
    }

    private static FileInfo[] GetExistingLogFiles(string baseLogFileName)
    {
        var logFileMask = Path.GetFileNameWithoutExtension(baseLogFileName) + "*" + Path.GetExtension(baseLogFileName);
        var logDirName = Path.GetDirectoryName(baseLogFileName);
        if (string.IsNullOrEmpty(logDirName))
            logDirName = Directory.GetCurrentDirectory();
        var logdir = new DirectoryInfo(logDirName);
        return logdir.Exists ? logdir.GetFiles(logFileMask, SearchOption.TopDirectoryOnly) : [];
    }

    internal void Close()
    {
        if (LogFileWriter != null)
        {
            var logWriter = LogFileWriter;
            LogFileWriter = null;

            logWriter.Dispose();
            LogFileStream.Dispose();
            LogFileStream = null;
        }
    }
}