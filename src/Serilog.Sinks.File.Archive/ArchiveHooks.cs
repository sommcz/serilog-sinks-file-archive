using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Serilog.Debugging;

namespace Serilog.Sinks.File.Archive
{
    /// <inheritdoc />
    /// <summary>
    /// Archives log files before they are deleted by Serilog's retention mechanism, copying them to another location
    /// and optionally compressing them using GZip
    /// </summary>
	public class ArchiveHooks : FileLifecycleHooks
	{
		private readonly CompressionLevel compressionLevel;
		private readonly int retainedFileCountLimit;
		private readonly string targetDirectory;
		private static readonly object ArchiveLock = new object();

		/// <summary>
		/// Create a new ArchiveHooks, which will archive completed log files before they are deleted by Serilog's retention mechanism
		/// </summary>
		/// <param name="compressionLevel">
		/// Level of GZIP compression to use. Use CompressionLevel.NoCompression if no compression is required
		/// </param>
		/// <param name="targetDirectory">
		/// Directory in which to archive files to. Use null if compressed, archived files should remain in the same folder
		/// </param>
		public ArchiveHooks(CompressionLevel compressionLevel = CompressionLevel.Fastest, string targetDirectory = null)
		{
			if (compressionLevel == CompressionLevel.NoCompression && targetDirectory == null)
                throw new ArgumentException($"Either {nameof(compressionLevel)} or {nameof(targetDirectory)} must be set");

			this.compressionLevel = compressionLevel;
			this.targetDirectory = targetDirectory;
		}

		/// <summary>
		/// Create a new ArchiveHooks, which will archive completed log files before they are deleted by Serilog's retention mechanism
		/// </summary>
		/// <param name="retainedFileCountLimit">Limit of Archived files.</param>
		/// <param name="compressionLevel">
		/// Level of GZIP compression to use. Use CompressionLevel.NoCompression if no compression is required
		/// </param>
		/// <param name="targetDirectory">
		/// Directory in which to archive files to. Use null if compressed, archived files should remain in the same folder
		/// </param>
        public ArchiveHooks(int retainedFileCountLimit, CompressionLevel compressionLevel = CompressionLevel.Fastest, string targetDirectory = null)
		{
			if (retainedFileCountLimit <= 0)
                throw new ArgumentException($"{nameof(retainedFileCountLimit)} must be greater than zero", nameof(retainedFileCountLimit));
			if (targetDirectory is not null && TokenExpander.IsTokenised(targetDirectory))
                throw new ArgumentException($"{nameof(targetDirectory)} must not be tokenised when using {nameof(retainedFileCountLimit)}", nameof(targetDirectory));
			if (compressionLevel == CompressionLevel.NoCompression && targetDirectory == null)
                throw new ArgumentException($"Either {nameof(compressionLevel)} or {nameof(targetDirectory)} must be set");

			this.compressionLevel = compressionLevel;
			this.retainedFileCountLimit = retainedFileCountLimit;
			this.targetDirectory = targetDirectory;
		}

		public override void OnFileDeleting(string path)
		{
			try
			{
				// Use .gz file extension if we are going to compress the source file
				var filename = this.compressionLevel != CompressionLevel.NoCompression
					? Path.GetFileName(path) + ".gz"
					: Path.GetFileName(path);

				// Determine the target path for the current file
				var currentTargetDir = this.targetDirectory != null
					? TokenExpander.Expand(this.targetDirectory)
					: Path.GetDirectoryName(path);

				lock (ArchiveLock)
				{
					// Create the target directory, if it doesn't already exist
					if (!Directory.Exists(currentTargetDir))
					{
						Directory.CreateDirectory(currentTargetDir!);
					}

					// Target file path
					var targetPath = Path.Combine(currentTargetDir, filename);

					// Do we need to compress the file, or simply copy it as-is?
					if (this.compressionLevel == CompressionLevel.NoCompression)
					{
						System.IO.File.Copy(path, targetPath, true);
					}
					else
					{
						using (var sourceStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var targetStream = new FileStream(targetPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
						using (var compressStream = new GZipStream(targetStream, this.compressionLevel))
						{
							sourceStream.CopyTo(compressStream);
						}
					}

					// Only apply archive file limit if we are archiving to a non-tokenised path (a constant path)
					if (this.retainedFileCountLimit > 0 && !this.IsArchivePathTokenised)
					{
						RemoveExcessFiles(currentTargetDir, path);
					}
				}
			}
			catch (Exception ex)
			{
				SelfLog.WriteLine("Error while archiving file {0}: {1}", path, ex);
				throw;
			}
		}

        private bool IsArchivePathTokenised => this.targetDirectory is not null && TokenExpander.IsTokenised(this.targetDirectory);

        private void RemoveExcessFiles(string folder, string filePath)
		{
			lock (ArchiveLock)
			{
				var baseFileName = Path.GetFileNameWithoutExtension(filePath).Split('_').First();
				var searchPattern = this.compressionLevel != CompressionLevel.NoCompression
					? "*.gz"
					: "*.*";

				var filesToDelete = Directory.GetFiles(folder, searchPattern)
					.Where(f => Path.GetFileNameWithoutExtension(f).StartsWith(baseFileName))
					.Select(f => new FileInfo(f))
                    .OrderByDescending(f => f, LogFileComparer.Default)
                    .Skip(this.retainedFileCountLimit)
                    .ToList();

				foreach (var file in filesToDelete)
				{
					try
					{
						file.Delete();
					}
					catch (Exception ex)
					{
						SelfLog.WriteLine("Error while deleting file {0}: {1}", file.FullName, ex);
					}
				}
			}
		}

		private class LogFileComparer : IComparer<FileInfo>
		{
			public static readonly IComparer<FileInfo> Default = new LogFileComparer();

			// This will not work correctly when the file uses a date format where lexicographical order does not
			// correspond to chronological order - but frankly, if you are using non ISO 8601 date formats in your
			// files you should be shot :)
			public int Compare(FileInfo x, FileInfo y)
			{
				if (x is null && y is null)
					return 0;
				if (x is null)
					return -1;
				if (y is null)
					return 1;
				if (x.Name.Length > y.Name.Length)
					return 1;
				if (y.Name.Length > x.Name.Length)
					return -1;

				return String.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
			}
		}
	}
}
