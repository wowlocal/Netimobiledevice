using Microsoft.Extensions.Logging;
using Netimobiledevice.Afc;
using Netimobiledevice.Lockdown;
using Netimobiledevice.Lockdown.Services;
using Netimobiledevice.Plist;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Netimobiledevice.InstallationProxy
{
    public sealed class InstallationProxyService : BaseService
    {
        protected override string ServiceName => "com.apple.mobile.installation_proxy";

        public InstallationProxyService(LockdownClient client) : base(client) { }

        public async Task<ArrayNode> Browse(DictionaryNode? options = null, ArrayNode? attributes = null, CancellationToken cancellationToken = default)
        {
            options ??= new DictionaryNode();
            if (attributes != null) {
                options.Add("ReturnAttributes", attributes);
            }

            DictionaryNode command = new DictionaryNode() {
                {"Command", new StringNode("Browse") },
                {"ClientOptions", options }
            };
            Service.SendPlist(command);

            ArrayNode result = new ArrayNode();
            while (true) {
                PropertyNode? response = await Service.ReceivePlistAsync(cancellationToken);
                if (response == null) {
                    break;
                }

                DictionaryNode responseDict = response.AsDictionaryNode();

                if (responseDict.ContainsKey("CurrentList")) {
                    ArrayNode data = responseDict["CurrentList"].AsArrayNode();
                    foreach (PropertyNode element in data) {
                        result.Add(element);
                    }
                }

                if (responseDict["Status"].AsStringNode().Value == "Complete") {
                    break;
                }
            }
            return result;
        }

        public async Task Install(string ipaPath, CancellationToken cancellationToken, Action<int>? callback = null, DictionaryNode? options = null)
        {
            await InstallFromLocal(ipaPath, "Install", cancellationToken, callback, options);
        }

        public async Task Upgrade(string ipaPath, CancellationToken cancellationToken, Action<int>? callback = null, DictionaryNode? options = null)
        {
            await InstallFromLocal(ipaPath, "Upgrade", cancellationToken, callback, options);
        }

        private async Task InstallFromLocal(string ipaPath, string command, CancellationToken cancellationToken, Action<int>? callback = null, DictionaryNode ? options = null)
        {
            byte[] ipaContents;
            Stopwatch sw = new Stopwatch();
            sw.Start();
            options ??= new DictionaryNode();
            var tmpRemoteIpaPath = "/sberinstaller.ipa";

            if (Directory.Exists(ipaPath)) {
                // Handle app directory conversion if necessary
                ipaContents = CreateIpaFromDirectory(ipaPath); // This is a placeholder method, replace with actual logic
            }
            else {
                ipaContents = await File.ReadAllBytesAsync(ipaPath, cancellationToken).ConfigureAwait(false);
            }
            sw.Stop();
            Logger.LogInformation("IPA read in {time}ms", sw.ElapsedMilliseconds);
            sw = new Stopwatch();
            sw.Start();
            // Use AFC to transfer the IPA to the device, this part depends on your AFC service implementation
            Action<int> firstHalfProgress = (int percent) => {
                callback?.Invoke(percent / 2);
            };
            using (var afc = new AfcService(this.Lockdown)) // Assuming `client` is an instance of `LockdownClient`
            {
                afc.SetFileContents(tmpRemoteIpaPath, ipaContents, cancellationToken, firstHalfProgress);
            }
            sw.Stop();
            Logger.LogInformation("IPA transferred in {time}ms", sw.ElapsedMilliseconds);

            DictionaryNode cmd = new DictionaryNode
            {
                { "Command", new StringNode(command) },
                { "ClientOptions", options },
                { "PackagePath", new StringNode(tmpRemoteIpaPath) }
            };
            sw = new Stopwatch();
            sw.Start();

            await Service.SendPlistAsync(cmd, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            Logger.LogInformation("Command sent in {time}ms", sw.ElapsedMilliseconds);
            sw = new Stopwatch();
            sw.Start();
            Action<int> secondHalfProgress = (int percent) => {
                callback?.Invoke(50 + percent / 2);
            };
            await WatchCompletion(cancellationToken, secondHalfProgress);
            sw.Stop();
            Logger.LogInformation("Watched completion in {time}ms", sw.ElapsedMilliseconds);
        }

        private async Task WatchCompletion(CancellationToken cancellationToken, Action<int>? callback = null)
        {
            Service.ExtendedTimeout();
            while (true) {
                PropertyNode? response = await Service.ReceivePlistAsync(cancellationToken).ConfigureAwait(false);
                if (response == null) {
                    break;
                }

                DictionaryNode responseDict = response.AsDictionaryNode();

                if (responseDict.TryGetValue("Error", out PropertyNode? errorNode)) {
                    Service.ResetTimeout();
                    throw new AppInstallException($"{errorNode.AsStringNode().Value}: {responseDict["ErrorDescription"].AsStringNode().Value}");
                }

                if (responseDict.TryGetValue("PercentComplete", out PropertyNode? completion)) {
                    int percent = (int) completion.AsIntegerNode().Value;
                    callback?.Invoke(percent);
                    Logger.LogInformation("{percent}% Complete", percent);
                }

                if (responseDict.TryGetValue("Status", out PropertyNode? status) && status.AsStringNode().Value == "Complete") {
                    Service.ResetTimeout();
                    return;
                }
            }
            Service.ResetTimeout();

            throw new AppInstallException("Installation or command did not complete successfully.");
        }

        private byte[] CreateIpaFromDirectory(string directoryPath)
        {
            // Implement your logic to create an IPA from the app directory
            throw new NotImplementedException("IPA creation from directory not implemented.");
        }

        /// <summary>
        /// Uninstalls the App with the given bundle identifier
        /// </summary>
        /// <param name="bundleIdentifier"></param>
        /// <param name="options"></param>
        /// <param name="callback"></param>
        /// <param name=""></param>
        /// <returns></returns>
        public async Task Uninstall(string bundleIdentifier, CancellationToken cancellationToken, DictionaryNode? options = null, Action<int>? callback = null)
        {
            DictionaryNode cmd = new DictionaryNode() {
                { "Command", new StringNode("Uninstall") },
                { "ApplicationIdentifier", new StringNode(bundleIdentifier) }
            };

            options ??= new DictionaryNode();
            cmd.Add("ClientOptions", options);

            await Service.SendPlistAsync(cmd, cancellationToken).ConfigureAwait(false);

            // Wait for the uninstall to complete
            // TODO self._watch_completion(handler, *args)

            while (true) {
                PropertyNode? response = await Service.ReceivePlistAsync(cancellationToken).ConfigureAwait(false);
                if (response == null) {
                    break;
                }

                DictionaryNode responseDict = response.AsDictionaryNode();
                if (responseDict.TryGetValue("Error", out PropertyNode? errorNode)) {
                    throw new AppInstallException($"{errorNode.AsStringNode().Value}: {responseDict["ErrorDescription"].AsStringNode().Value}");
                }

                if (responseDict.TryGetValue("PercentComplete", out PropertyNode? completion)) {
                    if (callback is not null) {
                        Logger.LogDebug("Using callback");
                        callback((int) completion.AsIntegerNode().Value);
                    }
                    Logger.LogInformation("Uninstall {percentComplete}% Complete", completion.AsIntegerNode().Value);
                }

                if (responseDict.TryGetValue("Status", out PropertyNode? status)) {
                    if (status.AsStringNode().Value == "Complete") {
                        return;
                    }
                }
            }
            throw new AppInstallException();
        }
    }
}
