﻿#region Related components
using System;
using System.Net;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Configuration;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.StaticFiles;

using Microsoft.Extensions.Logging;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WampSharp.V2.Core.Contracts;

using net.vieapps.Components.Caching;
using net.vieapps.Components.Security;
using net.vieapps.Components.Utility;
#endregion

namespace net.vieapps.Services.SRP
{
	public class Handler
	{
		RequestDelegate Next { get; }
		bool RedirectToNoneWWW { get; set; } = true;
		bool RedirectToHTTPS { get; set; } = false;
		string DefaultDirectory { get; set; } = "apps";
		string DefaultFile { get; set; } = "index.html";
		Dictionary<string, Map> RedirectMaps { get; } = new Dictionary<string, Map>(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, Map> ForwardMaps { get; } = new Dictionary<string, Map>(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, Map> DirectoryMaps { get; } = new Dictionary<string, Map>(StringComparer.OrdinalIgnoreCase);

		public Handler(RequestDelegate next)
		{
			this.Next = next;
			if (ConfigurationManager.GetSection("net.vieapps.services.srp.maps") is AppConfigurationSectionHandler svcConfig)
			{
				// global settings
				this.RedirectToNoneWWW = "true".IsEquals(svcConfig.Section.Attributes["redirectToNoneWWW"]?.Value);
				this.RedirectToHTTPS = "true".IsEquals(svcConfig.Section.Attributes["redirectToHTTPS"]?.Value);
				this.DefaultDirectory = svcConfig.Section.Attributes["defaultDirectory"]?.Value ?? "apps";
				this.DefaultDirectory = Path.IsPathRooted(this.DefaultDirectory) ? this.DefaultDirectory : Path.Combine(Global.RootPath, this.DefaultDirectory);
				this.DefaultFile = svcConfig.Section.Attributes["defaultFile"]?.Value ?? "index.html";

				// individual settings
				if (svcConfig.Section.SelectNodes("map") is System.Xml.XmlNodeList maps)
					maps.ToList()
						.Where(info => !string.IsNullOrWhiteSpace(info.Attributes["host"]?.Value))
						.Select(info => new Map
						{
							Host = info.Attributes["host"].Value,
							RedirectTo = info.Attributes["redirectTo"]?.Value,
							ForwardTo = info.Attributes["forwardTo"]?.Value,
							ForwardTokenName = info.Attributes["forwardTokenName"]?.Value,
							ForwardTokenValue = info.Attributes["forwardTokenValue"]?.Value,
							Directory = info.Attributes["directory"]?.Value,
							NotFound = info.Attributes["notFound"]?.Value,
							RedirectToNoneWWW = !string.IsNullOrWhiteSpace(info.Attributes["redirectToNoneWWW"]?.Value) ? "true".IsEquals(info.Attributes["redirectToNoneWWW"]?.Value) : this.RedirectToNoneWWW,
							RedirectToHTTPS = !string.IsNullOrWhiteSpace(info.Attributes["redirectToHTTPS"]?.Value) ? "true".IsEquals(info.Attributes["redirectToHTTPS"]?.Value) : this.RedirectToHTTPS
						})
						.Where(map => !string.IsNullOrWhiteSpace(map.RedirectTo) || !string.IsNullOrWhiteSpace(map.ForwardTo) || !string.IsNullOrWhiteSpace(map.Directory))
						.ForEach(map =>
						{
							if (!string.IsNullOrWhiteSpace(map.RedirectTo))
							{
								var location = map.RedirectTo.Trim();
								while (location.EndsWith("/"))
									location = location.Left(location.Length - 1);
								if (!location.IsStartsWith("http://") && !location.IsStartsWith("https://"))
									location = "https://" + location;
								map.Host.Trim().ToLower().ToArray("|", true).ForEach(host => this.RedirectMaps[host] = map.Clone(m =>
								{
									m.Host = host;
									m.RedirectTo = location;
								}));
							}
							else if (!string.IsNullOrWhiteSpace(map.ForwardTo))
							{
								var location = map.ForwardTo.Trim();
								while (location.EndsWith("/"))
									location = location.Left(location.Length - 1);
								if (!location.IsStartsWith("http://") && !location.IsStartsWith("https://"))
									location = "https://" + location;
								map.Host.Trim().ToLower().ToArray("|", true).ForEach(host => this.ForwardMaps[host] = map.Clone(m =>
								{
									m.Host = host;
									m.ForwardTo = location;
								}));
							}
							else
							{
								var directory = map.Directory;
								if (directory.IndexOf(Path.DirectorySeparatorChar) < 0)
									directory = Path.Combine(this.DefaultDirectory, directory);
								directory = Directory.Exists(directory) ? directory : Path.Combine(this.DefaultDirectory, directory);
								map.Host.Trim().ToLower().ToArray("|", true).ForEach(host => this.DirectoryMaps[host] = map.Clone(m =>
								{
									m.Host = host;
									m.Directory = directory;
								}));
							}
						});
			}
			Global.Logger.LogInformation("Settings:" + "\r\n" +
				$"=> Redirect to none WWW: {this.RedirectToNoneWWW}" + "\r\n" +
				$"=> Redirect to HTTPs: {this.RedirectToHTTPS}" + "\r\n" +
				$"=> Default directory: {this.DefaultDirectory}" + "\r\n" +
				$"=> Default file: {this.DefaultFile}" + "\r\n" +
				$"=> Redirect maps: {(this.RedirectMaps.Count < 1 ? "None" : $"\r\n\t+ {string.Join("\r\n\t+ ", this.RedirectMaps.Select(m => $"{m.Key} => {m.Value.RedirectTo}"))}")}" + "\r\n" +
				$"=> Forward maps: {(this.ForwardMaps.Count < 1 ? "None" : $"\r\n\t+ {string.Join("\r\n\t+ ", this.ForwardMaps.Select(m => $"{m.Key} => {m.Value.ForwardTo + $" ({m.Value.ForwardTokenName ?? "None"}/{m.Value.ForwardTokenValue ?? "None"})"}"))}")}" + "\r\n" +
				$"=> Directory maps: {(this.DirectoryMaps.Count < 1 ? "None" : $"\r\n\t+ {string.Join("\r\n\t+ ", this.DirectoryMaps.Select(m => $"{m.Key} => {m.Value.Directory + $" ({m.Value.RedirectToNoneWWW}/{m.Value.RedirectToHTTPS}/{m.Value.NotFound ?? "None"})"}"))}")}"
			);
		}

		public async Task Invoke(HttpContext context)
		{
			// load balancing health check
			if (context.Request.Path.Value.IsEquals("/load-balancing-health-check"))
				await context.WriteAsync("OK", "text/plain", null, 0, null, TimeSpan.Zero, null, Global.CancellationTokenSource.Token).ConfigureAwait(false);

			else
			{
				// process the request
				await this.ProcessRequestAsync(context).ConfigureAwait(false);

				// invoke next middleware
				try
				{
					await this.Next.Invoke(context).ConfigureAwait(false);
				}
				catch (InvalidOperationException) { }
				catch (Exception ex)
				{
					Global.Logger.LogCritical($"Error occurred while invoking the next middleware: {ex.Message}", ex);
				}
			}
		}

		#region Process request
		bool GetMap(string host, Dictionary<string, Map> maps, out Map map)
		   => maps.TryGetValue(host, out map)
			   ? true
			   : host.IsStartsWith("www.")
				   ? maps.TryGetValue(host.Right(host.Length - 4), out map)
				   : false;

		Task ProcessRequestAsync(HttpContext context)
		{
			var requestUri = context.GetRequestUri();
			if (this.GetMap(requestUri.Host, this.RedirectMaps, out Map map))
			{
				context.RedirectPermanently($"{new Uri(map.RedirectTo + requestUri.PathAndQuery)}");
				return Task.CompletedTask;
			}
			else if (this.GetMap(requestUri.Host, this.ForwardMaps, out map))
			{
				return context.WriteAsync($"Forward to => {new Uri(map.ForwardTo + requestUri.PathAndQuery + (requestUri.PathAndQuery.IndexOf("?") > -1 ? "&" : "?") + map.ForwardTokenName + "=" + map.ForwardTokenValue.UrlEncode())}", Global.CancellationTokenSource.Token);
			}
			else
				return this.ProcessDirectoryRequestAsync(context);
		}

		async Task ProcessDirectoryRequestAsync(HttpContext context)
		{
			//  prepare
			context.Items["PipelineStopwatch"] = Stopwatch.StartNew();
			var requestUri = context.GetRequestUri();
			if (Global.IsVisitLogEnabled)
				await context.WriteVisitStartingLogAsync().ConfigureAwait(false);

			// request of static files
			if (Global.StaticSegments.Contains(requestUri.GetRequestPathSegments().First()))
				await context.ProcessStaticFileRequestAsync().ConfigureAwait(false);

			// only allow GET method
			else if (!context.Request.Method.IsEquals("GET"))
				context.ShowHttpError((int)HttpStatusCode.MethodNotAllowed, $"Method {context.Request.Method} is not allowed", "MethodNotAllowedException", context.GetCorrelationID());

			// process
			else
				try
				{
					var fileInfo = await this.ProcessFileRequestAsync(context).ConfigureAwait(false);
					if (fileInfo != null && Global.IsDebugLogEnabled)
						await context.WriteLogsAsync("SRP", $"Success response ({fileInfo.FullName} - {fileInfo.Length:#,##0} bytes)").ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					await context.WriteLogsAsync("SRP", $"Failure response [{requestUri}]", ex).ConfigureAwait(false);
					context.ShowHttpError(ex.GetHttpStatusCode(), ex.Message, ex.GetType().GetTypeName(true), context.GetCorrelationID(), ex, Global.IsDebugLogEnabled);
				}

			if (Global.IsVisitLogEnabled)
				await context.WriteVisitFinishingLogAsync().ConfigureAwait(false);
		}

		async Task<FileInfo> ProcessFileRequestAsync(HttpContext context)
		{
			// prepare
			var requestUri = context.GetRequestUri();
			this.GetMap(requestUri.Host, this.DirectoryMaps, out Map map);

			// redirect
			var redirectToHttps = (map != null ? map.RedirectToHTTPS : this.RedirectToHTTPS) && !requestUri.Scheme.IsEquals("https");
			var redirectToNoneWWW = (map != null ? map.RedirectToNoneWWW : this.RedirectToNoneWWW) && requestUri.Host.StartsWith("www.");
			if (redirectToHttps || redirectToNoneWWW)
			{
				var url = $"{requestUri}";
				url = redirectToHttps ? url.Replace("http://", "https://") : url;
				url = redirectToNoneWWW ? url.Replace("://www.", "://") : url;
				if (Global.IsDebugLogEnabled)
					await context.WriteLogsAsync("SRP", $"Redirect: {requestUri} => {url}");
				context.Redirect(url);
				return null;
			}

			// prepare file info
			FileInfo fileInfo = null;
			var filePath = $"{map?.Directory ?? this.DefaultDirectory}/{string.Join("/", requestUri.GetRequestPathSegments())}".Replace(@"\", "/").Replace("//", "/").Replace('/', Path.DirectorySeparatorChar);
			filePath += filePath.EndsWith(Path.DirectorySeparatorChar) ? this.DefaultFile : "";

			// check to reduce traffic
			var eTag = "SRP#" + $"{requestUri}".ToLower().GenerateUUID();
			if (eTag.IsEquals(context.GetHeaderParameter("If-None-Match")))
			{
				var isNotModified = true;
				var lastModifed = DateTime.Now.ToUnixTimestamp();
				if (context.GetHeaderParameter("If-Modified-Since") != null)
				{
					fileInfo = new FileInfo(filePath);
					if (fileInfo.Exists)
					{
						lastModifed = fileInfo.LastWriteTime.ToUnixTimestamp();
						isNotModified = lastModifed <= context.GetHeaderParameter("If-Modified-Since").FromHttpDateTime().ToUnixTimestamp();
					}
					else
						isNotModified = false;
				}
				if (isNotModified)
				{
					context.SetResponseHeaders((int)HttpStatusCode.NotModified, eTag, lastModifed, "public", context.GetCorrelationID());
					if (Global.IsDebugLogEnabled)
						await context.WriteLogsAsync("SRP", $"Success response with status code 304 to reduce traffic ({filePath})").ConfigureAwait(false);
					return fileInfo;
				}
			}

			// check existed
			fileInfo = fileInfo ?? new FileInfo(filePath);
			if (!fileInfo.Exists && !string.IsNullOrWhiteSpace(map?.NotFound))
				fileInfo = new FileInfo(Path.Combine(Path.IsPathRooted(map.Directory) ? map.Directory : Path.Combine(this.DefaultDirectory, map.Directory), map.NotFound));

			if (!fileInfo.Exists)
			{
				if (Global.IsDebugLogEnabled)
					await context.WriteLogsAsync("SRP", $"Requested file is not found [{requestUri}] => [{fileInfo.FullName}]").ConfigureAwait(false);
				throw new FileNotFoundException($"Not Found [{requestUri}]");
			}

			// prepare body
			var fileMimeType = fileInfo.GetMimeType();
			var fileContent = fileMimeType.IsEndsWith("json")
				? JObject.Parse(await UtilityService.ReadTextFileAsync(fileInfo, null, Global.CancellationTokenSource.Token).ConfigureAwait(false)).ToString(Formatting.Indented).ToBytes()
				: await UtilityService.ReadBinaryFileAsync(fileInfo, Global.CancellationTokenSource.Token).ConfigureAwait(false);

			// response
			context.SetResponseHeaders((int)HttpStatusCode.OK, new Dictionary<string, string>
			{
				{ "Content-Type", $"{fileMimeType}; charset=utf-8" },
				{ "ETag", eTag },
				{ "Last-Modified", $"{fileInfo.LastWriteTime.ToHttpString()}" },
				{ "Cache-Control", "public" },
				{ "Expires", $"{DateTime.Now.AddMinutes(13).ToHttpString()}" },
				{ "X-CorrelationID", context.GetCorrelationID() }
			});
			await context.WriteAsync(fileContent, Global.CancellationTokenSource.Token).ConfigureAwait(false);
			return fileInfo;
		}
		#endregion

		#region Helper: WAMP connections
		internal static void OpenWAMPChannels(int waitingTimes = 6789)
		{
			Global.Logger.LogDebug($"Attempting to connect to WAMP router [{new Uri(WAMPConnections.GetRouterStrInfo()).GetResolvedURI()}]");
			Global.OpenWAMPChannels(
				(sender, args) =>
				{
					Global.Logger.LogDebug($"Incoming channel to WAMP router is established - Session ID: {args.SessionId}");
					WAMPConnections.IncomingChannel.Update(WAMPConnections.IncomingChannelSessionID, Global.ServiceName, $"Incoming ({Global.ServiceName} HTTP service)");
					Global.InterCommunicateMessageUpdater?.Dispose();
					Global.InterCommunicateMessageUpdater = WAMPConnections.IncomingChannel.RealmProxy.Services
						.GetSubject<CommunicateMessage>("net.vieapps.rtu.communicate.messages.srp")
						.Subscribe(
							async message => await Handler.ProcessInterCommunicateMessageAsync(message).ConfigureAwait(false),
							async exception => await Global.WriteLogsAsync(Global.Logger, "RTU", $"{exception.Message}", exception).ConfigureAwait(false)
						);
				},
				(sender, args) =>
				{
					Global.Logger.LogDebug($"Outgoing channel to WAMP router is established - Session ID: {args.SessionId}");
					WAMPConnections.OutgoingChannel.Update(WAMPConnections.OutgoingChannelSessionID, Global.ServiceName, $"Outgoing ({Global.ServiceName} HTTP service)");
					Task.Run(async () =>
					{
						try
						{
							await Task.WhenAll(
								Global.InitializeLoggingServiceAsync(),
								Global.InitializeRTUServiceAsync()
							).ConfigureAwait(false);
							Global.Logger.LogInformation("Helper services are succesfully initialized");
							while (WAMPConnections.IncomingChannel == null || WAMPConnections.OutgoingChannel == null)
								await Task.Delay(UtilityService.GetRandomNumber(234, 567), Global.CancellationTokenSource.Token).ConfigureAwait(false);
						}
						catch (Exception ex)
						{
							Global.Logger.LogError($"Error occurred while initializing helper services: {ex.Message}", ex);
						}
					})
					.ContinueWith(async task => await Global.RegisterServiceAsync().ConfigureAwait(false), TaskContinuationOptions.OnlyOnRanToCompletion)
					.ConfigureAwait(false);
				},
				waitingTimes
			);
		}

		internal static void CloseWAMPChannels(int waitingTimes = 1234)
		{
			Global.UnregisterService(waitingTimes);
			Global.InterCommunicateMessageUpdater?.Dispose();
			WAMPConnections.CloseChannels();
		}

		static Task ProcessInterCommunicateMessageAsync(CommunicateMessage message) => Task.CompletedTask;
		#endregion

	}

	[Serializable]
	public class Map
	{
		public Map() { }
		public string Host { get; set; } = "";
		public string RedirectTo { get; set; } = null;
		public string ForwardTo { get; set; } = null;
		public string ForwardTokenName { get; set; } = null;
		public string ForwardTokenValue { get; set; } = null;
		public string Directory { get; set; } = "";
		public string NotFound { get; set; } = null;
		public bool RedirectToNoneWWW { get; set; } = false;
		public bool RedirectToHTTPS { get; set; } = false;
	}
}