﻿using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using MonoTools.Library;
using MonoTools.Debugger;
using MonoTools.Debugger.VisualStudio;
using MonoTools.VisualStudio.MonoClient;
using MonoTools.VisualStudio.Views;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NLog;
using IServiceProvider = Microsoft.VisualStudio.OLE.Interop.IServiceProvider;
using Task = System.Threading.Tasks.Task;
using Microsoft.MIDebugEngine;
using System.Windows;
using Microsoft.VisualStudio.Web.Application;

namespace MonoTools.VisualStudio {

	public partial class Services {

		[System.Diagnostics.Conditional("DEBUG")]
		void Dump(Properties props) {
			foreach (Property p in props) {
				try {
					System.Diagnostics.Debugger.Log(1, "", $"Property: {p.Name}={p.Value.ToString()}\r\n");
				} catch { }
			}
		}

		[System.Diagnostics.Conditional("DEBUG")]
		void Dump(Project proj) {
			System.Diagnostics.Debugger.Log(1, "", "project.Properties");
			Dump(proj.Properties);
			System.Diagnostics.Debugger.Log(1, "", "proj.ConfigurationManager.ActiveConfiguration.Properties");
			Dump(proj.ConfigurationManager.ActiveConfiguration.Properties);
		}

		public async void Start() {
			try {
				BuildSolution();

				string target = GetStartupAssemblyPath();
				string exe = null;
				string outputDirectory = Path.GetDirectoryName(target);
				string url = null;
				string serverurl = null;
				string page = null;
				string workingDirectory = null;
				string arguments = null;
				Project startup = GetStartupProject();
				var props = startup.ConfigurationManager.ActiveConfiguration.Properties;

				//Dump(startup);

				bool isWeb = ((object[])startup.ExtenderNames).Any(x => x.ToString() == "WebApplication") || startup.Object is VsWebSite.VSWebSite;

				var isNet4 = true;
				var frameworkprop = props.Get("TargetFrameworkMoniker")
					?.Split(',')
					.Where(t => t.StartsWith("Version="))
					.Select(t => t.Substring("Version=".Length))
					.FirstOrDefault();
				isNet4 = (frameworkprop == null || string.Compare(frameworkprop, "v4.0") >= 0);

				var action = "0";
				action = props.Get("StartAction");
				exe = props.Get("StartProgram");
				arguments = props.Get("StartArguments");
				workingDirectory = props.Get("StartWorkingDirectory");
				url = props.Get("StartURL");
				page = props.Get("StartPage");

				if (isWeb) {
					var ext = (WAProjectExtender)startup.Extender["WebApplication"];
					action = ext.DebugStartAction.ToString();
					outputDirectory = new Uri(ext.OpenedURL).LocalPath;
					serverurl = ext.BrowseURL ?? ext.NonSecureUrl ?? ext.SecureUrl ?? ext.IISUrl ?? "http://127.0.0.1:9000";
					var uri = new Uri(serverurl);
					var port = uri.Port;
					if (ext.UseIIS) { // when running IIS, use random xsp port
						port = 15000 + new Random(target.GetHashCode()).Next(5000);
						uri = new Uri($"{uri.Scheme}://{uri.Host}:{port}");
						serverurl = uri.AbsoluteUri;
					}
					var args = $"-v --port={port}";
					var ssl = uri.Scheme.StartsWith("https");
					if (ssl) args += MonoWebProcess.SSLXpsArguments();

					var monoBin = Path.Combine(DetermineMonoPath(), "bin\\xsp" + (isNet4 ? "4" : "") + ".bat");
					var xsp = new ProcessStartInfo(monoBin, args) {
						WorkingDirectory = outputDirectory,
						UseShellExecute = false,
						CreateNoWindow = false
					};
					System.Diagnostics.Process.Start(xsp);

					if (action == "2") {
						var run = new ProcessStartInfo(ext.StartExternalProgram, ext.StartCmdLineArguments) {
							WorkingDirectory = ext.StartWorkingDirectory,
							UseShellExecute = false,
							CreateNoWindow = false,
							WindowStyle = ProcessWindowStyle.Minimized
						};
						System.Diagnostics.Process.Start(run);
						return;
					} else if (action == "3") {
						System.Diagnostics.Process.Start(ext.StartExternalUrl);
						return;
					} else if (action == "0") {
						System.Diagnostics.Process.Start(ext.CurrentDebugUrl);
					} else if (action == "1") {
						System.Diagnostics.Process.Start(ext.StartPageUrl);
					}
				} else {
					if (action == "0" || action == "2") {
						exe = target;
					}

					var monoBin = Path.Combine(DetermineMonoPath(), "bin\\mono.exe");
					var info = new ProcessStartInfo(monoBin, $"\"{exe}\" {arguments}") {
						WorkingDirectory = workingDirectory,
						UseShellExecute = false,
						CreateNoWindow = false
					};
					System.Diagnostics.Process.Start(info);

					if (action == "2") System.Diagnostics.Process.Start(url);
				}
			} catch (Exception ex) {
				logger.Error(ex);
				if (Server != null) Server.Stop();
				MessageBox.Show(ex.Message, "MonoTools.Debugger", MessageBoxButton.OK, MessageBoxImage.Error);
			}
		}

		public async void StartDebug() {
			try {
				var startup = GetStartupProject();
				bool isWeb = ((object[])startup.ExtenderNames).Any(x => x.ToString() == "WebApplication") || startup.Object is VsWebSite.VSWebSite;
				string host = null;
				if (isWeb) {
					var ext = (WAProjectExtender)startup.Extender["WebApplication"];
					var serverurl = ext.BrowseURL ?? ext.NonSecureUrl ?? ext.SecureUrl ?? ext.IISUrl ?? "http://127.0.0.1:9000";
					var uri = new Uri(serverurl);
					if (uri.Host == "*" || uri.Host == "") host = "*";
					else {
						host = uri.Host;
						var localips = Dns.GetHostAddresses(host).Concat(Dns.GetHostAddresses(Environment.MachineName));
						var local = host == "localhost" || string.Compare(host, Environment.MachineName, true) == 0 || 
							Dns.GetHostAddresses(host).Any(a => a.IsIPv6LinkLocal || a == IPAddress.Parse("127.0.0.1") || localips.Any(b => a == b));
						if (local) host = null;
					}
				} else {
					var props = startup.ConfigurationManager.ActiveConfiguration.Properties;
					try {
						if (props.Get("RemoteDebugEnabled")?.ToLower()  == "true") {
							host = props.Get("RemoteDebugMachine");
						}
					} catch { }
				}
				StartDebug(host);
			} catch (Exception ex) {
				logger.Error(ex);
				if (Server != null) Server.Stop();
				MessageBox.Show(ex.Message, "MonoTools.Debugger", MessageBoxButton.OK, MessageBoxImage.Error);
			}
		}

		public static MonoDebugServer Server = null;

		public async void StartDebug(string host) {
			try {
				if (Server != null) {
					Server.Stop();
					Server = null;
				}

				BuildSolution();

				if (host == null) {
					Server = new MonoDebugServer(true);
					Server.Start();
					await AttachDebugger(Library.MonoProcess.GetLocalIp().ToString(), true);
				} else if (host == "*" || host == "?" || host == "") StartSearching();
				else {
					await AttachDebugger(host, false);
				}
			} catch (Exception ex) {
				logger.Error(ex);
				if (Server != null) Server.Stop();
				MessageBox.Show(ex.Message, "MonoTools.Debugger", MessageBoxButton.OK, MessageBoxImage.Error);
			}
		}

		public async void StartSearching() {
			var dlg = new ServersFound();

			if (dlg.ShowDialog().GetValueOrDefault()) {
				try {
					BuildSolution();
					if (dlg.ViewModel.SelectedServer != null)
						await AttachDebugger(dlg.ViewModel.SelectedServer.IpAddress.ToString());
					else if (!string.IsNullOrWhiteSpace(dlg.ViewModel.ManualIp))
						await AttachDebugger(dlg.ViewModel.ManualIp);
				} catch (Exception ex) {
					logger.Error(ex);
					MessageBox.Show(ex.Message, "MonoTools.Debugger", MessageBoxButton.OK, MessageBoxImage.Error);
				}
			}
		}

		Task consoleTask;

		public async Task AttachDebugger(string host, bool local = false) {
			string target = GetStartupAssemblyPath();
			string exe = null, debug = null;
			string outputDirectory = Path.GetDirectoryName(target);
			string url = null;
			string serverurl = null;
			string page = null;
			string workingDirectory = null;
			string arguments = null;
			Project startup = GetStartupProject();
			var props = startup.ConfigurationManager.ActiveConfiguration.Properties;

			//Dump(startup);
			
			bool isWeb = ((object[])startup.ExtenderNames).Any(x => x.ToString() == "WebApplication") || startup.Object is VsWebSite.VSWebSite;

			var isNet4 = true;
			var frameworkprop = props.Get("TargetFrameworkMoniker")
				?.Split(',')
				.Where(t => t.StartsWith("Version="))
				.Select(t => t.Substring("Version=".Length))
				.FirstOrDefault();
			isNet4 = (frameworkprop == null || string.Compare(frameworkprop, "v4.0") >= 0);
			Frameworks framework = isNet4 ? Frameworks.Net4 : Frameworks.Net2;

			var action = "0";
			action = props.Get("StartAction");
			exe = props.Get("StartProgram");
			arguments = props.Get("StartArguments");
			workingDirectory = props.Get("StartWorkingDirectory");
			url = props.Get("StartURL");
			page = props.Get("StartPage");

			var ports = Options.Ports;
			var password = Options.Password;

			if (isWeb) {
				outputDirectory = Path.GetDirectoryName(outputDirectory);
				var ext = (WAProjectExtender)startup.Extender["WebApplication"];
				action = ext.DebugStartAction.ToString();
				serverurl = ext.BrowseURL ?? ext.NonSecureUrl ?? ext.SecureUrl ?? ext.IISUrl ?? "http://127.0.0.1:9000";
				var uri = new Uri(serverurl);
				var port = uri.Port;
				if (ext.UseIIS) { // when running IIS, use random xsp port
					port = 15000 + new Random(target.GetHashCode()).Next(5000);
					uri = new Uri($"{uri.Scheme}://{uri.Host}:{port}");
					serverurl = uri.AbsoluteUri;
				}

				await StartDebugger(target, null, ApplicationTypes.WebApplication, framework, outputDirectory, null, serverurl, local, host, ports, password);

				if (action == "2") {
					var run = new ProcessStartInfo(ext.StartExternalProgram, ext.StartCmdLineArguments) {
						WorkingDirectory = ext.StartWorkingDirectory,
						UseShellExecute = false,
						CreateNoWindow = false
					};
					System.Diagnostics.Process.Start(run);
					return;
				} else if (action == "3") {
					System.Diagnostics.Process.Start(ext.StartExternalUrl);
					return;
				} else if (action == "0") {
					System.Diagnostics.Process.Start(ext.CurrentDebugUrl);
				} else if (action == "1") {
					System.Diagnostics.Process.Start(ext.StartPageUrl);
				}
			} else {

				var outputType = startup.Properties.Get("OutputType"); // output type (0: windows app, 1: console app, 2: class lib)
				if (outputType == "2") throw new InvalidOperationException("Cannot start a class library.");

				var appType = outputType == "0" ? ApplicationTypes.WindowsApplication : ApplicationTypes.ConsoleApplication;

				await StartDebugger(target, arguments, appType, framework, outputDirectory, workingDirectory, null, local, host, ports, password);

				if (action == "2") System.Diagnostics.Process.Start(url);
				else if (action == "1") {
					var run = new ProcessStartInfo(exe, arguments) {
						WorkingDirectory = workingDirectory,
						UseShellExecute = false,
						CreateNoWindow = false
					};
					System.Diagnostics.Process.Start(run);
				}
			}
		}

		public async Task StartDebugger(string targetExe, string arguments, ApplicationTypes appType, Frameworks framework, string outputDir, string workingDir, string url, bool local, string host, string ports, string password) {

			// resolve host
			IPAddress ip; 
			if (!IPAddress.TryParse(host, out ip)) {
				IPAddress[] adresses = Dns.GetHostEntry(host).AddressList;
				host = adresses.FirstOrDefault(adr => adr.AddressFamily == AddressFamily.InterNetwork)?.ToString();
				if (host == null) throw new ArgumentException("Remote server not found.");
			}
			// setup communication
			var client = new DebugClient(local, ports, password);
			var session = await client.ConnectToServerAsync(host);
			// send execute request
			await session.ExecuteAsync(targetExe, arguments, appType, framework, Path.GetFullPath(outputDir), workingDir, url);
			// read response

			var cancel = local ? MonoDebugServer.Current.Cancel : null;

			var response = await session.WaitForAnswerAsync(cancel.Token);
			if (cancel.IsCancellationRequested) return;

			if (!(response is StatusMessage)) throw new InvalidOperationException($"Wrong response message type {response.GetType().FullName}.");
			var status = (StatusMessage)response;
			if (status.Command == Library.Commands.Exit) return;
			if (status.Command == Library.Commands.InvalidPassword) {
				throw new InvalidOperationException("Invalid debug server password.");
			} else if (status.Command == Library.Commands.Exception) {
				throw new Exception($"Server Exception:\r\n{status.ExceptionType}\r\n{status.ExceptionMessage}\r\n{status.StackTrace}");
			} else if (status.Command != Library.Commands.Started) {
				throw new Exception($"Invalid response \"{status.Command}\" from server.");
			}

			// handle console output responses
			consoleTask = Task.Run(async () => { 
				try {
					var msg = await session.WaitForAnswerAsync<StatusMessage>(cancel.Token);
					if (cancel.IsCancellationRequested) return;
					while (msg?.Command == Library.Commands.Info) {
						var text = msg.OutputText ?? msg.ErrorText;
						if (!string.IsNullOrEmpty(text)) {
							Output.Text(text);
							Debug.Write(text);
						}
						msg = await session.WaitForAnswerAsync<StatusMessage>(cancel.Token);
						if (cancel.IsCancellationRequested) return;
					}
					if (msg?.Command == Library.Commands.Exception) {
						logger.Error($"Server Exception:\r\n{status.ExceptionType}\r\n{msg.ExceptionMessage}\r\n{msg.StackTrace}");
					} else if (msg?.Command == Library.Commands.Exit) {
						DebuggedProcess.Instance.Detach();
					} else	session.PushBack(msg);
				} catch { }
			});

			// start debugger
			IntPtr pInfo = GetDebugInfo($"{host}:{client.DebuggerPort}", Path.GetFileName(targetExe), outputDir);
			var sp = new ServiceProvider((IServiceProvider)dte);
			try {
				var dbg = (IVsDebugger)sp.GetService(typeof(SVsShellDebugger));
				int hr = dbg.LaunchDebugTargets(1, pInfo);
				Marshal.ThrowExceptionForHR(hr);

				DebuggedProcess.Instance.AssociateDebugSession(session);
			} catch (Exception ex) {
				logger.Error(ex);
				string msg;
				var sh = (IVsUIShell)sp.GetService(typeof(SVsUIShell));
				sh.GetErrorInfo(out msg);

				if (!string.IsNullOrWhiteSpace(msg)) logger.Error(msg);

				throw;
			} finally {
				if (pInfo != IntPtr.Zero) Marshal.FreeCoTaskMem(pInfo);
			}
		}

		private IntPtr GetDebugInfo(string args, string targetExe, string outputDirectory) {
			var info = new VsDebugTargetInfo();
			info.cbSize = (uint)Marshal.SizeOf(info);
			info.dlo = DEBUG_LAUNCH_OPERATION.DLO_CreateProcess;

			info.bstrExe = Path.Combine(outputDirectory, targetExe);
			info.bstrCurDir = outputDirectory;
			info.bstrArg = args; // no command line parameters
			info.bstrRemoteMachine = null; // debug locally
			info.grfLaunch = (uint)__VSDBGLAUNCHFLAGS.DBGLAUNCH_StopDebuggingOnEnd;
			info.fSendStdoutToOutputWindow = 0;
			info.clsidCustom = AD7Guids.EngineGuid;
			info.grfLaunch = 0;

			IntPtr pInfo = Marshal.AllocCoTaskMem((int)info.cbSize);
			Marshal.StructureToPtr(info, pInfo, false);
			return pInfo;
		}

		public void OpenLogFile() {
			if (File.Exists(MonoLogger.LoggerPath)) {
				System.Diagnostics.Process.Start(MonoLogger.LoggerPath);
			}
		}

	}
}