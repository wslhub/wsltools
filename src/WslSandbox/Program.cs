using System;
using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Tasks;
using System.Threading;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;

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

            var tarGzFilePath = default(string);

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

                tarGzFilePath = Path.Combine(downloadDirectoryPath, $"{tempDistroName}.tar.gz");
                var data = await OpenRootFilesystemStream(rootFs, cts.Token);

                if (ReferenceEquals(data.Item2, Stream.Null))
                    throw new Exception("Cannot load target root file system image.");

                await Console.Out.WriteLineAsync("Downloading distribution tarball archive...");

                using (var contentStream = data.Item2)
                using (var fileStream = File.OpenWrite(tarGzFilePath))
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

                var distroInstallPath = Path.Combine(basePath, tempDistroName);

                await Console.Out.WriteLineAsync($"Install temporary distro '{tempDistroName}' on '{distroInstallPath}' (Version: {setVersion}).");

                if (Directory.Exists(distroInstallPath))
                    Directory.Delete(distroInstallPath, true);

                if (!Directory.Exists(distroInstallPath))
                    Directory.CreateDirectory(distroInstallPath);

                var psi = new ProcessStartInfo(wslPath,
                    $"--import {tempDistroName} \"{distroInstallPath}\" \"{tarGzFilePath}\" --version {setVersion}")
                {
                    UseShellExecute = false,
                };

                var process = Process.Start(psi);

                if (process == null)
                    throw new Exception("Cannot register the temporary distro.");

                await WaitForExitAsync(process, cts.Token);

                psi = new ProcessStartInfo(wslPath,
                    $"--distribution {tempDistroName} -- {string.Join(" ", args)}")
                {
                    UseShellExecute = false,
                };

                process = Process.Start(psi);

                if (process == null)
                    throw new Exception("Cannot launch the temporary distro.");

                await WaitForExitAsync(process, cts.Token);

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
                if (!string.IsNullOrWhiteSpace(tarGzFilePath) &&
                    File.Exists(tarGzFilePath))
                {
                    File.Delete(tarGzFilePath);
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

        private static async Task<Tuple<long?, Stream>> OpenRootFilesystemStream(string rootFs, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(rootFs))
                rootFs = "alpine-315";

            switch (rootFs)
            {
                case "alpine-315":
                    rootFs = "https://dl-cdn.alpinelinux.org/alpine/v3.15/releases/x86_64/alpine-minirootfs-3.15.0-x86_64.tar.gz";
                    break;

                case "ubuntu-1804":
                    rootFs = "https://cloud-images.ubuntu.com/bionic/current/bionic-server-cloudimg-amd64-wsl.rootfs.tar.gz";
                    break;

                case "ubuntu-2004":
                    rootFs = "https://cloud-images.ubuntu.com/focal/current/focal-server-cloudimg-amd64-wsl.rootfs.tar.gz";
                    break;

                default:
                    return new Tuple<long?, Stream>(null, Stream.Null);
            }

            if (!Uri.TryCreate(rootFs, UriKind.Absolute, out var parsedUri))
                return new Tuple<long?, Stream>(null, Stream.Null);

            if (parsedUri.IsFile)
            {
                var fileInfo = new FileInfo(parsedUri.LocalPath);
                return new Tuple<long?, Stream>(fileInfo.Length, fileInfo.OpenRead());
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
                    return new Tuple<long?, Stream>(null, Stream.Null);

                return new Tuple<long?, Stream>(
                    response.Content.Headers.ContentLength,
                    await response.Content.ReadAsStreamAsync().ConfigureAwait(false));
            }

            return new Tuple<long?, Stream>(null, Stream.Null);
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
