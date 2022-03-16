using SharpCompress.Compressors.Xz;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WslSandbox
{
    internal static class Program
    {
        /// <summary>
        /// Application Entrypoint
        /// </summary>
        /// <param name="rootFs">Specify rootfs URL or known name - Default is alpine-315</param>
        /// <param name="setVersion">Set WSL version (1 or 2)</param>
        /// <param name="args">Following arguments</param>
        /// <returns>This method returns an exit code</returns>
        [STAThread]
        private static async Task<int> Main(
            string[] args,
            string rootFs = default,
            int setVersion = 2)
        {
            CancellationTokenSource cts = null;

            ConsoleCancelEventHandler cancelEventHandler = async (_, e) =>
            {
                try
                {
                    await Console.Error.WriteLineAsync("Canceling...").ConfigureAwait(false);
                    cts?.Cancel();
                }
                catch { }
                finally { e.Cancel = true; }
            };

            var ng = new NamesGenerator();
            var tempDistroName = ng.GetRandomName();
            var wslPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");

            var downloadedFilePath = default(string);
            var processedFilePath = default(string);

            try
            {
                cts = new CancellationTokenSource();
                Console.CancelKeyPress += cancelEventHandler;

                var basePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WslSandbox");

                if (!Directory.Exists(basePath))
                    Directory.CreateDirectory(basePath);

                var downloadDirectoryPath = Path.Combine(basePath, "Downloads");

                if (!Directory.Exists(downloadDirectoryPath))
                    Directory.CreateDirectory(downloadDirectoryPath);

                var data = await OpenRootFilesystemStream(rootFs, cts.Token);

                if (ReferenceEquals(data.Item2, Stream.Null))
                    throw new Exception("Cannot load target root file system image.");

                var extension = ".dat";
                switch (data.Item3)
                {
                    case RootFsContentType.Tar:
                        extension = ".tar";
                        break;

                    case RootFsContentType.TarGz:
                        extension = ".tar.gz";
                        break;

                    case RootFsContentType.TarXz:
                        extension = ".tar.xz";
                        break;

                    default:
                        extension = ".dat";
                        break;
                }

                downloadedFilePath = Path.Combine(downloadDirectoryPath, $"{tempDistroName}.{extension.TrimStart('.')}");

                await Console.Out.WriteLineAsync("Downloading distribution tarball archive...");

                using (var contentStream = data.Item2)
                using (var fileStream = File.OpenWrite(downloadedFilePath))
                using (var progress = new ProgressBar())
                {
                    progress.Report(0d);

                    byte[] buffer = new byte[65536];
                    long total = 0L;
                    int read = 0;

                    while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length, cts.Token)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, read, cts.Token);

                        if (data.Item1.HasValue)
                        {
                            progress.Report(total / (double)data.Item1.Value);
                            total += read;
                        }
                    }

                    progress.Report(1d);
                }

                await Console.Out.WriteLineAsync("Processing distribution tarball conversion...");

                if (data.Item3 == RootFsContentType.TarGz)
                    processedFilePath = downloadedFilePath;
                else if (data.Item3 == RootFsContentType.Tar)
                    processedFilePath = downloadedFilePath;
                else if (data.Item3 == RootFsContentType.TarXz)
                {
                    // Decompress tar.xz file to .tar file
                    processedFilePath = Path.Combine(downloadDirectoryPath, $"{tempDistroName}.tar");

                    using (var contentStream = File.OpenRead(downloadedFilePath))
                    using (var xzStream = new XZStream(contentStream))
                    using (var fileStream = File.OpenWrite(processedFilePath))
                    {
                        await xzStream.CopyToAsync(fileStream, 64000, cts.Token);
                    }

                    if (File.Exists(downloadedFilePath))
                        File.Delete(downloadedFilePath);
                }
                else
                    throw new Exception("Cannot load root file system image due to unsupported format.");

                var distroInstallPath = Path.Combine(basePath, tempDistroName);

                await Console.Out.WriteLineAsync($"Install temporary distro '{tempDistroName}' on '{distroInstallPath}' (Version: {setVersion}).");

                if (Directory.Exists(distroInstallPath))
                    Directory.Delete(distroInstallPath, true);

                if (!Directory.Exists(distroInstallPath))
                    Directory.CreateDirectory(distroInstallPath);

                var psi = new ProcessStartInfo(wslPath,
                    $"--import {tempDistroName} \"{distroInstallPath}\" \"{processedFilePath}\" --version {setVersion}")
                {
                    UseShellExecute = false,
                };

                var process = Process.Start(psi);

                if (process != null)
                    await WaitForExitAsync(process, cts.Token);

                if (process == null || process.ExitCode != 0)
                    throw new Exception("Cannot register the temporary distro.");

                psi = new ProcessStartInfo(wslPath,
                    $"--distribution {tempDistroName} -- {string.Join(" ", args)}")
                {
                    UseShellExecute = false,
                };

                process = Process.Start(psi);

                if (process != null)
                    await WaitForExitAsync(process, cts.Token);

                if (process == null || process.ExitCode != 0)
                    throw new Exception("Cannot launch the temporary distro.");

                return 0;
            }
            catch (Exception ex)
            {
                var rootException = UnwrapException(ex);

                if (rootException != null)
                    await Console.Error.WriteLineAsync(rootException.Message);
                else
                    await Console.Error.WriteLineAsync("Unknown error occurred.");

                return 1;
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(downloadedFilePath) &&
                    File.Exists(downloadedFilePath))
                {
                    File.Delete(downloadedFilePath);
                }

                if (!string.IsNullOrWhiteSpace(processedFilePath) &&
                    File.Exists(processedFilePath))
                {
                    File.Delete(processedFilePath);
                }

                var psi = new ProcessStartInfo(wslPath,
                    $"--unregister {tempDistroName}")
                {
                    UseShellExecute = false,
                };

                var process = Process.Start(psi);

                if (process != null)
                    await WaitForExitAsync(process, cts.Token);
                else
                    await Console.Error.WriteLineAsync("Cannot unregister the temporary distro.");

                if (cts != null)
                    cts.Dispose();

                Console.CancelKeyPress -= cancelEventHandler;
            }
        }

        private static Exception UnwrapException(Exception ex)
        {
            if (ex == null)
                return ex;

            if (ex is TargetInvocationException)
                return ExceptionDispatchInfo.Capture(ex.InnerException).SourceException;

            if (ex is AggregateException)
                return ((AggregateException)ex).InnerException;

            return ex;
        }

        private static async Task<Tuple<long?, Stream, RootFsContentType>> OpenRootFilesystemStream(string rootFs, CancellationToken cancellationToken = default)
        {
            var contentType = default(RootFsContentType);

            if (string.IsNullOrWhiteSpace(rootFs))
                rootFs = "alpine-315";

            switch (rootFs)
            {
                case "alpine-315":
                    rootFs = "https://dl-cdn.alpinelinux.org/alpine/v3.15/releases/x86_64/alpine-minirootfs-3.15.0-x86_64.tar.gz";
                    contentType = RootFsContentType.TarGz;
                    break;

                case "ubuntu-1804":
                    rootFs = "https://cloud-images.ubuntu.com/bionic/current/bionic-server-cloudimg-amd64-wsl.rootfs.tar.gz";
                    contentType = RootFsContentType.TarGz;
                    break;

                case "ubuntu-2004":
                    rootFs = "https://cloud-images.ubuntu.com/focal/current/focal-server-cloudimg-amd64-wsl.rootfs.tar.gz";
                    contentType = RootFsContentType.TarGz;
                    break;

                default:
                    return new Tuple<long?, Stream, RootFsContentType>(null, Stream.Null, contentType);
            }

            if (!Uri.TryCreate(rootFs, UriKind.Absolute, out var parsedUri))
                return new Tuple<long?, Stream, RootFsContentType>(null, Stream.Null, contentType);

            if (parsedUri.IsFile)
            {
                var fileInfo = new FileInfo(parsedUri.LocalPath);
                return new Tuple<long?, Stream, RootFsContentType>(fileInfo.Length, fileInfo.OpenRead(), contentType);
            }

            if (string.Equals(Uri.UriSchemeHttps, parsedUri.Scheme, StringComparison.Ordinal) ||
                string.Equals(Uri.UriSchemeHttp, parsedUri.Scheme, StringComparison.Ordinal))
            {
                var httpClient = new HttpClient(new HttpClientHandler()
                {
                    UseDefaultCredentials = true,
                    AllowAutoRedirect = true,
                });

                var requestMessage = new HttpRequestMessage(HttpMethod.Get, rootFs);
                var response = await httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                    return new Tuple<long?, Stream, RootFsContentType>(null, Stream.Null, contentType);

                return new Tuple<long?, Stream, RootFsContentType>(
                    response.Content.Headers.ContentLength,
                    await response.Content.ReadAsStreamAsync().ConfigureAwait(false),
                    contentType);
            }

            return new Tuple<long?, Stream, RootFsContentType>(null, Stream.Null, contentType);
        }

        private static Task WaitForExitAsync(Process process, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (process.HasExited)
                return Task.CompletedTask;

            var tcs = new TaskCompletionSource<object>();
            process.EnableRaisingEvents = true;
            process.Exited += (sender, args) => tcs.TrySetResult(null);

            if (cancellationToken != default(CancellationToken))
                cancellationToken.Register(() => tcs.SetCanceled());

            return process.HasExited ? Task.CompletedTask : tcs.Task;
        }
    }
}
