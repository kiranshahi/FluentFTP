﻿using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using FluentFTP.Streams;
using FluentFTP.Helpers;
using FluentFTP.Exceptions;
using FluentFTP.Client.Modules;
using System.Threading;
using System.Threading.Tasks;

namespace FluentFTP {
	public partial class AsyncFtpClient {

		/// <summary>
		/// Downloads the specified file onto the local file system asynchronously.
		/// High-level API that takes care of various edge cases internally.
		/// Supports very large files since it downloads data in chunks.
		/// </summary>
		/// <param name="localPath">The full or relative path to the file on the local file system</param>
		/// <param name="remotePath">The full or relative path to the file on the server</param>
		/// <param name="existsMode">Overwrite if you want the local file to be overwritten if it already exists. Append will also create a new file if it doesn't exists</param>
		/// <param name="verifyOptions">Sets if checksum verification is required for a successful download and what to do if it fails verification (See Remarks)</param>
		/// <param name="progress">Provide an implementation of IProgress to track download progress.</param>
		/// <param name="token">The token that can be used to cancel the entire process</param>
		/// <returns>FtpStatus flag indicating if the file was downloaded, skipped or failed to transfer.</returns>
		/// <remarks>
		/// If verification is enabled (All options other than <see cref="FtpVerify.None"/>) the hash will be checked against the server.  If the server does not support
		/// any hash algorithm, then verification is ignored.  If only <see cref="FtpVerify.OnlyChecksum"/> is set then the return of this method depends on both a successful 
		/// upload &amp; verification.  Additionally, if any verify option is set and a retry is attempted then overwrite will automatically be set to true for subsequent attempts.
		/// </remarks>
		public async Task<FtpStatus> DownloadFile(string localPath, string remotePath, FtpLocalExists existsMode = FtpLocalExists.Resume, FtpVerify verifyOptions = FtpVerify.None, IProgress<FtpProgress> progress = null, CancellationToken token = default(CancellationToken)) {
			// verify args
			if (localPath.IsBlank()) {
				throw new ArgumentException("Required parameter is null or blank.", nameof(localPath));
			}

			if (remotePath.IsBlank()) {
				throw new ArgumentException("Required parameter is null or blank.", nameof(remotePath));
			}

			return await DownloadFileToFileAsync(localPath, remotePath, existsMode, verifyOptions, progress, token, new FtpProgress(1, 0));
		}

		/// <summary>
		/// Download a remote file to a local file
		/// </summary>
		protected async Task<FtpStatus> DownloadFileToFileAsync(string localPath, string remotePath, FtpLocalExists existsMode, FtpVerify verifyOptions, IProgress<FtpProgress> progress, CancellationToken token, FtpProgress metaProgress) {

			// verify args
			if (localPath.IsBlank()) {
				throw new ArgumentException("Required parameter is null or blank.", nameof(localPath));
			}

			if (remotePath.IsBlank()) {
				throw new ArgumentException("Required parameter is null or blank.", nameof(remotePath));
			}

			// skip downloading if the localPath is a folder
			if (LocalPaths.IsLocalFolderPath(localPath)) {
				throw new ArgumentException("Local path must specify a file path and not a folder path.", nameof(localPath));
			}

			remotePath = remotePath.GetFtpPath();

			LogFunction(nameof(DownloadFile), new object[] { localPath, remotePath, existsMode, verifyOptions });


			bool isAppend = false;

			// skip downloading if the local file exists
			long knownFileSize = 0;
			long restartPos = 0;
#if NETSTANDARD || NET5_0_OR_GREATER
			if (existsMode == FtpLocalExists.Resume && await Task.Run(() => File.Exists(localPath), token)) {
				knownFileSize = (await GetFileSize(remotePath, -1, token));
				restartPos = await FtpFileStream.GetFileSizeAsync(localPath, false, token);
				if (knownFileSize.Equals(restartPos)) {
#else
			if (existsMode == FtpLocalExists.Resume && File.Exists(localPath)) {
				knownFileSize = (await GetFileSize(remotePath, -1, token));
				restartPos = FtpFileStream.GetFileSize(localPath, false);
				if (knownFileSize.Equals(restartPos)) {
#endif
					LogWithPrefix(FtpTraceLevel.Info, "Skipping file because Resume is enabled and file is fully downloaded (Remote: " + remotePath + ", Local: " + localPath + ")");
					return FtpStatus.Skipped;
				}
				else {
					isAppend = true;
				}
			}
#if NETSTANDARD || NET5_0_OR_GREATER
			else if (existsMode == FtpLocalExists.Skip && await Task.Run(() => File.Exists(localPath), token)) {
#else
			else if (existsMode == FtpLocalExists.Skip && File.Exists(localPath)) {
#endif
				LogWithPrefix(FtpTraceLevel.Info, "Skipping file because Skip is enabled and file already exists locally (Remote: " + remotePath + ", Local: " + localPath + ")");
				return FtpStatus.Skipped;
			}

			try {
				// create the folders
				var dirPath = Path.GetDirectoryName(localPath);
#if NETSTANDARD || NET5_0_OR_GREATER
				if (!string.IsNullOrWhiteSpace(dirPath) && !await Task.Run(() => Directory.Exists(dirPath), token)) {
#else
				if (!string.IsNullOrWhiteSpace(dirPath) && !Directory.Exists(dirPath)) {
#endif
					Directory.CreateDirectory(dirPath);
				}
			}
			catch (Exception ex1) {
				// catch errors creating directory
				throw new FtpException("Error while creating directories. See InnerException for more info.", ex1);
			}

			// if not appending then fetch remote file size since mode is determined by that
			/*if (knownFileSize == 0 && !isAppend) {
				knownFileSize = GetFileSize(remotePath);
			}*/

			bool downloadSuccess;
			var verified = true;
			var attemptsLeft = verifyOptions.HasFlag(FtpVerify.Retry) ? Config.RetryAttempts : 1;
			do {

				// download the file from the server to a file stream or memory stream
				downloadSuccess = await DownloadFileInternalAsync(localPath, remotePath, null, restartPos, progress, token, metaProgress, knownFileSize, isAppend, 0);
				attemptsLeft--;

				if (!downloadSuccess) {
					LogWithPrefix(FtpTraceLevel.Info, "Failed to download file.");

					if (attemptsLeft > 0)
						LogWithPrefix(FtpTraceLevel.Info, "Retrying to download file.");
				}

				// if verification is needed
				if (downloadSuccess && verifyOptions != FtpVerify.None) {
					verified = await VerifyTransferAsync(localPath, remotePath, token);
					LogWithPrefix(FtpTraceLevel.Info, "File Verification: " + (verified ? "PASS" : "FAIL"));
					if (!verified && attemptsLeft > 0) {
						LogWithPrefix(FtpTraceLevel.Verbose, "Retrying due to failed verification." + (existsMode == FtpLocalExists.Resume ? "  Overwrite will occur." : "") + "  " + attemptsLeft + " attempts remaining");
						// Force overwrite if a retry is required
						existsMode = FtpLocalExists.Overwrite;
					}
				}
			} while ((!downloadSuccess || !verified) && attemptsLeft > 0);

			if (downloadSuccess && !verified && verifyOptions.HasFlag(FtpVerify.Delete)) {
				File.Delete(localPath);
			}

			if (downloadSuccess && !verified && verifyOptions.HasFlag(FtpVerify.Throw)) {
				throw new FtpException("Downloaded file checksum value does not match remote file");
			}

			return downloadSuccess && verified ? FtpStatus.Success : FtpStatus.Failed;
		}

	}
}
