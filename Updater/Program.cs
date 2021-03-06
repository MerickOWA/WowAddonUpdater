﻿using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Win32;

namespace AddonUpdater
{
	internal static class Program
	{
		private static void Main(string[] args)
		{
			MainAsync(args).GetAwaiter().GetResult();
		}
		private static async Task MainAsync(string[] args)
		{
			try
			{
				var addonDirectory = ConfigurationManager.AppSettings["AddonFolder"];
				var wowFolderTemplate = GetWowFolderTemplate();

				//await DownloadFiles(addonDirectory);

				string toWowDirectory(string type) => wowFolderTemplate.Replace("{type}", type);
				var addons = EnumerateAddonFiles(addonDirectory).Select(o => new Addon(o)).OrderBy(o => o.Archive).ToList();

				var types = addons.Select(o => o.Type).Distinct();

				Console.WriteLine("Installing updates...");
				var unused = new[] { "_retail_", "_classic_" }.SelectMany(type => GetExistingAddons(type, toWowDirectory(type))).ToHashSet();

				var conflicts = (
					from addon in addons
					let type = addon.Type
					from folder in addon.Folders
					group addon by (type,folder)
					into folderAddons
					where folderAddons.Count() > 1
					from addon in folderAddons
					select addon).Distinct().ToList();

				foreach (var addon in conflicts)
				{
					Console.WriteLine($"ERROR: {Path.GetFileName(addon.Archive)} conflicts!");
					unused.RemoveAll(addon.Folders.Select(o => (addon.Type, o)));
				}

				var ranUpdate = false;
				foreach (var addon in addons.Except(conflicts))
				{
					var wowDirectory = toWowDirectory(addon.Type);
					var installedVersion = GetInstalledVersion(addon.Folders, wowDirectory);

					if (addon.Version != installedVersion)
					{
						ranUpdate = true;

						foreach (var folder in addon.Folders)
						{
							var path = Path.Combine(wowDirectory, folder);
							if (Directory.Exists(path))
							{
								Console.WriteLine($"! {folder}");
								Directory.Delete(path, true);
							}
							else
							{
								Console.WriteLine($"> {folder}");
							}
						}

						ZipFile.ExtractToDirectory(addon.Archive, wowDirectory);
					}

					unused.RemoveAll(addon.Folders.Select(o => (addon.Type, o)));
				}

				foreach (var item in unused)
				{
					ranUpdate = true;
					Console.WriteLine($"< {item.type}\\{item.folder}");
					Directory.Delete(Path.Combine(toWowDirectory(item.type), item.folder), true);
				}

				Console.WriteLine(ranUpdate ? "Done." : "No updates needed.");
			}
			catch (Exception e)
			{
				Console.WriteLine($"ERROR: {e}");
			}

			Console.ReadKey(true);
		}

		private static string GetWowFolderTemplate()
		{
			var installPath = (string)Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Blizzard Entertainment\World of Warcraft", "InstallPath", null);
			if (installPath == null || !installPath.EndsWith(@"\_retail_\"))
				throw new Exception("World of Warcraft install not found?");

			var basePath = installPath.Substring(0, installPath.Length - 10);

			return @$"{basePath}\{{type}}\Interface\AddOns";
		}

		private static IEnumerable<string> EnumerateAddonFiles(string addonDirectory)
		{
			return Directory.EnumerateFiles(addonDirectory, "*.zip", SearchOption.AllDirectories).Where(o => !Path.GetFileName(o).StartsWith("_"));
		}

		private static async Task DownloadFiles(string addonDirectory)
		{
			var downloadFile = Path.Combine(addonDirectory, "Download.xml");
			var lastWriteTime = (DateTimeOffset)File.GetLastWriteTimeUtc(downloadFile);
			var document = XDocument.Load(downloadFile);
			var root = document.Root;
			var addons = root.Elements("Addon");

			var lastCheckDate = ParseDateTimeOffset(root.Attribute("lastCheck")?.Value);
			var now = DateTimeOffset.Now;
			if (lastCheckDate == null || now - lastCheckDate > TimeSpan.FromMinutes(10) || addons.Any(o => o.Attribute("file")?.Value == null))
			{
				Console.WriteLine("Checking for updates...");

				var files = (await Task.WhenAll(addons.Select(o => Task.Run(() => DownloadFile(addonDirectory, o))))).Where(o => o != null);

				root.SetAttributeValue("lastCheck", lastCheckDate = now);
				document.Save(downloadFile);

				var extraFiles = EnumerateAddonFiles(addonDirectory).Except(files);
				foreach (var extraFile in extraFiles)
				{
					Console.WriteLine($"Deleting {Path.GetFileName(extraFile)}");
					File.Delete(extraFile);
				}
			}
		}

		private static async Task<string> GetCurseForgeUrl(string type, string url)
		{
			var addon = Path.GetFileName(Path.GetDirectoryName(url));

			var web = new HtmlAgilityPack.HtmlWeb();
			var doc = await web.LoadFromWebAsync(url);

			var links = doc.DocumentNode.SelectNodes("//table/tbody/tr")
				.Where(o => o.SelectSingleNode("td[1]").InnerText.Trim() == "R")
				.Select(o => new
				{
					url = o.SelectSingleNode("td[2]/a").GetAttributeValue("href", ""),
					version = Regex.Replace(o.SelectSingleNode("td[5]").InnerText, "\\s+", "")
				});

			switch (type)
			{
				case "_retail_": links = links.Where(o => !o.version.StartsWith("1.")); break;
				case "_classic_": links = links.Where(o => o.version.StartsWith("1.") || o.version.EndsWith("+1")); break;
			}

			var link =  links.FirstOrDefault();
			if (link == null)
			{
				return null;
			}

			var relativeUrl = link.url.Replace("files", "download") + "/file";

			return new Uri(new Uri(url), relativeUrl).AbsoluteUri;
		}

		private static Task<string> GetDownloadUrl(string type, string url)
		{
			return url.Contains("curseforge.com") ? GetCurseForgeUrl(type, url) : Task.FromResult(url);
		}

		private static async Task<string> DownloadFile(string parentDirectory, XElement addon)
		{
			var type = addon.Attribute("type")?.Value;
			var url = addon.Attribute("url")?.Value;
			var existingName = addon.Attribute("file")?.Value;
			var existingSize = ParseLong(addon.Attribute("size")?.Value);
			var existingLastModified = ParseDateTimeOffset(addon.Attribute("lastModified")?.Value);
			var addonDirectory = Path.Combine(parentDirectory, type);
			var existingPath = existingName != null ? Path.Combine(addonDirectory, existingName) : null;

			using (var client = new HttpClient())
			{
				client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/77.0.3865.90 Safari/537.36");

				var downloadUrl = await GetDownloadUrl(type, url);
				if (downloadUrl == null)
				{
					Console.WriteLine($"ERROR: No download found for {type}: {url}");
					return null;
				}

				var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, downloadUrl) { Headers = { IfModifiedSince = existingLastModified } });

				if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
				{
					return Path.Combine(addonDirectory, existingName);
				}

				var fileName = response.Content.Headers.ContentDisposition?.FileName.Trim('"') ?? Path.GetFileName(UrlDecode(response.RequestMessage.RequestUri.LocalPath));
				var fileSize = response.Content.Headers.ContentLength;
				var lastModified = response.Content.Headers.LastModified;
				var path = Path.Combine(addonDirectory, fileName);

				if (!File.Exists(existingPath) || fileName != existingName || fileSize != existingSize || lastModified != existingLastModified)
				{
					Console.WriteLine($"Downloading {fileName}");

					DeleteIfExists(addonDirectory, existingName);

					var stream = await response.Content.ReadAsStreamAsync();

					using (var file = File.OpenWrite(path))
					{
						stream.CopyTo(file);
					}

					addon.SetAttributeValue("file", fileName);
					addon.SetAttributeValue("size", fileSize);
					addon.SetAttributeValue("lastModified", lastModified);
				}

				return path;
			}
		}

		private static string UrlDecode(string str)
		{
			return Uri.UnescapeDataString(str.Replace("+", " "));
		}

		private static long? ParseLong(string value)
		{
			return long.TryParse(value, out var result) ? (long?)result : null;
		}

		private static DateTimeOffset? ParseDateTimeOffset(string value)
		{
			return DateTimeOffset.TryParse(value, out var result) ? (DateTimeOffset?)result : null;
		}

		private static void DeleteIfExists(string directory, string file)
		{
			if (file != null)
			{
				var path = Path.Combine(directory, file);

				if (File.Exists(path))
				{
					File.Delete(path);
				}
			}
		}

		private static string GetInstalledVersion(IEnumerable<string> directories, string wowDirectory)
		{
			return CreateHashForFolder(directories.Aggregate(Enumerable.Empty<(string, Stream)>(), (result, path) =>
				{
					var fullpath = Path.Combine(wowDirectory, path);
					return Directory.Exists(fullpath) ? result.Concat(Directory.EnumerateFiles(fullpath, "*", SearchOption.AllDirectories).Select(o => (o.Substring(wowDirectory.Length + 1), (Stream)File.OpenRead(o)))) : result;
				}
			));
		}

		private class Addon
		{
			public Addon(string file)
			{
				using (var zip = ZipFile.OpenRead(file))
				{
					Archive = file;

					Folders = zip.Entries.Select(o => GetBaseFolder(o.FullName)).Distinct().ToHashSet();

					Version = CreateHashForFolder(zip.Entries.Where(o => !o.FullName.EndsWith("/")).Select(o => (o.FullName.Replace('/', '\\'), o.Open())));
				}
			}

			public string Type { get { return Path.GetFileName(Path.GetDirectoryName(Archive)); } }

			public string Archive { get; }

			public IReadOnlyCollection<string> Folders { get; }

			public string Version { get; }
		}

		private static string CreateHashForFolder(IEnumerable<(string file, Stream stream)> files)
		{
			var hash = SHA1.Create();

			var buffer = new byte[4096];

			foreach (var item in files.OrderBy(o => o.file))
			{
				using (item.stream)
				{
					// hash path
					var pathBytes = Encoding.UTF8.GetBytes(item.file.ToLower());
					hash.TransformBlock(pathBytes, 0, pathBytes.Length, pathBytes, 0);

					// hash contents
					int read;
					while ((read = item.stream.Read(buffer, 0, buffer.Length)) > 0)
					{
						hash.TransformBlock(buffer, 0, read, buffer, 0);
					}
				}
			}

			hash.TransformFinalBlock(new byte[0], 0, 0);

			return BitConverter.ToString(hash.Hash).Replace("-", "").ToLower();
		}

		private static string GetBaseFolder(string path)
		{
			return path.Split('/', '\\')[0];
		}

		private static IEnumerable<(string type, string folder)> GetExistingAddons(string type, string wowDirectory)
		{
			string toFullPath(string folder) => Path.Combine(wowDirectory, folder);

			var keepFile = toFullPath(".keep");
			var keepAddons = File.Exists(keepFile) ? File.ReadLines(keepFile).Select(toFullPath).ToHashSet() : null;

			return Directory.EnumerateDirectories(wowDirectory)
				.Where(path =>
					(keepAddons == null || !keepAddons.Contains(path))
					&& !Directory.Exists(Path.Combine(path, ".git"))
					&& !File.Exists(Path.Combine(path, ".keep")))
				.Select(o => (type, o.Substring(wowDirectory.Length + 1)));
		}

		private static HashSet<T> ToHashSet<T>(this IEnumerable<T> collection)
		{
			return new HashSet<T>(collection);
		}

		private static void RemoveAll<T>(this ICollection<T> set, IEnumerable<T> items)
		{
			foreach (var item in items)
			{
				set.Remove(item);
			}
		}
	}
}
