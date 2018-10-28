using System;
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
				var wowDirectory = ConfigurationManager.AppSettings["WowFolder"];

				await DownloadFiles(addonDirectory);

				var addons = EnumerateAddonFiles(addonDirectory).Select(o => new Addon(o)).OrderBy(o => o.Archive).ToList();

				Console.WriteLine("Installing updates...");
				var unused = GetExistingAddons(wowDirectory).ToHashSet();

				var conflicts = (
					from addon in addons
					from folder in addon.Folders
					group addon by folder
					into folderAddons
					where folderAddons.Count() > 1
					from addon in folderAddons
					select addon).Distinct().ToList();

				foreach (var addon in conflicts)
				{
					Console.WriteLine($"ERROR: {Path.GetFileName(addon.Archive)} conflicts!");
					unused.RemoveAll(addon.Folders);
				}

				var ranUpdate = false;
				foreach (var addon in addons.Except(conflicts))
				{
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

					unused.RemoveAll(addon.Folders);
				}

				foreach (var folder in unused)
				{
					ranUpdate = true;
					Console.WriteLine($"< {folder}");
					Directory.Delete(Path.Combine(wowDirectory, folder), true);
				}

				Console.WriteLine(ranUpdate ? "Done." : "No updates needed.");
			}
			catch (Exception e)
			{
				Console.WriteLine($"ERROR: {e}");
			}

			Console.ReadKey(true);
		}

		private static IEnumerable<string> EnumerateAddonFiles(string addonDirectory)
		{
			return Directory.EnumerateFiles(addonDirectory, "*.zip").Where(o => !Path.GetFileName(o).StartsWith("_"));
		}

		private static async Task DownloadFiles(string addonDirectory)
		{
			var downloadFile = Path.Combine(addonDirectory, "Download.xml");
			var lastWriteTime = (DateTimeOffset)File.GetLastWriteTimeUtc(downloadFile);
			var document = XDocument.Load(downloadFile);
			var root = document.Root;

			var lastCheckDate = ParseDateTimeOffset(root.Attribute("lastCheck")?.Value);
			var now = DateTimeOffset.Now;
			if (lastCheckDate == null || now - lastCheckDate > TimeSpan.FromMinutes(10))
			{
				Console.WriteLine("Checking for updates...");

				var addons = root.Elements("Addon");

				var files = await Task.WhenAll(addons.Select(o => Task.Run(() => DownloadFile(addonDirectory, o))));

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

		private static async Task<string> DownloadFile(string addonDirectory, XElement addon)
		{
			var url = addon.Attribute("url")?.Value;
			var existingName = addon.Attribute("file")?.Value;
			var existingSize = ParseLong(addon.Attribute("size")?.Value);
			var existingLastModified = ParseDateTimeOffset(addon.Attribute("lastModified")?.Value);
			var existingPath = Path.Combine(addonDirectory, existingName);

			using (var client = new HttpClient())
			{
				var response = await client.GetAsync(url);

				var fileName = response.Content.Headers.ContentDisposition?.FileName.Trim('"') ?? Path.GetFileName(response.RequestMessage.RequestUri.LocalPath);
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

		private static readonly Regex versionRegex = new Regex(@"^\s+DisplayVersion\s*=\s*""([^""]*)""");
		private static readonly Regex revisionRegex = new Regex(@"^\s+ReleaseRevision\s*=\s*(\d+)");

		private static string GetDBMVersion(Stream stream)
		{
			var lines = ReadLines(stream).ToList();

			var version = versionRegex.Match(lines.Single(o => versionRegex.IsMatch(o))).Groups[1].Value;

			var revision = revisionRegex.Match(lines.Single(o => revisionRegex.IsMatch(o))).Groups[1].Value;
			return $"{version}-r{revision}";
		}

		private static string GetAddOnMetadata(this ZipArchiveEntry entry, string type)
		{
			var prefix = $"## {type}:";
			return ReadLines(entry.Open()).FirstOrDefault(o => o.StartsWith(prefix))?.Substring(prefix.Length).Trim();
		}

		private static IEnumerable<string> GetExistingAddons(string wowDirectory)
		{
			string toFullPath(string folder) => Path.Combine(wowDirectory, folder);

			var keepFile = toFullPath(".keep");
			var keepAddons = File.Exists(keepFile) ? File.ReadLines(keepFile).Select(toFullPath).ToHashSet() : null;

			return Directory.EnumerateDirectories(wowDirectory)
				.Where(path =>
					(keepAddons == null || !keepAddons.Contains(path))
					&& !Directory.Exists(Path.Combine(path, ".git"))
					&& !File.Exists(Path.Combine(path, ".keep")))
				.Select(o => o.Substring(wowDirectory.Length + 1));
		}

		private static readonly Regex escapeSequences = new Regex(@"\|(?:c[0-9a-fA-F]{8}|r)");
		private static string StripUIEscape(string text)
		{
			return escapeSequences.Replace(text, "");
		}

		private static IEnumerable<string> ReadLines(Stream stream)
		{
			using (var reader = new StreamReader(stream))
			{
				string line;
				while ((line = reader.ReadLine()) != null)
				{
					yield return line;
				}
			}
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
