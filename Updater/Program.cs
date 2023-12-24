using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace AddonUpdater
{
	internal static class Program
	{
		private static void Log(string message) => Console.WriteLine($"{DateTime.Now:yyyy-MM-dd'T'HH:mm:ss.ff}: {message}");

		private static async Task Main(string[] args)
		{
			try
			{
				var addonDirectory = ConfigurationManager.AppSettings["AddonFolder"];
				var wowFolderTemplate = GetWowFolderTemplate();

				Log("Getting addons...");
				string toWowDirectory(string type) => wowFolderTemplate.Replace("{type}", type);
				var addons = await GetAddons(addonDirectory);

				var types = addons.Select(o => o.Type).Distinct();

				Log("Installing updates...");
				var unused = new[] { "_retail_", "_classic_" }.SelectMany(type => GetExistingAddons(type, toWowDirectory(type))).ToHashSet();

				var conflicts = (
					from addon in addons
					let type = addon.Type
					from folder in addon.Folders
					group addon by (type, folder)
					into folderAddons
					where folderAddons.Count() > 1
					from addon in folderAddons
					select addon).Distinct().ToList();

				foreach (var addon in conflicts)
				{
					Log($"ERROR: {Path.GetFileName(addon.Archive)} conflicts!");
					unused.RemoveAll(addon.Folders.Select(o => (addon.Type, o)));
				}

				foreach (var addon in addons.Except(conflicts))
				{
					var wowDirectory = toWowDirectory(addon.Type);
					var installedVersion = GetInstalledVersion(addon.Folders, wowDirectory);

					if (addon.Version != installedVersion)
					{
						foreach (var folder in addon.Folders)
						{
							var path = Path.Combine(wowDirectory, folder);
							if (Directory.Exists(path))
							{
								Log($"! {addon.Type}\\{folder}");
								Directory.Delete(path, true);
							}
							else
							{
								Log($"> {addon.Type}\\{folder}");
							}
						}

						ZipFile.ExtractToDirectory(addon.Archive, wowDirectory);
					}

					unused.RemoveAll(addon.Folders.Select(o => (addon.Type, o)));
				}

				Log("Removing old addons...");
				foreach (var (type, folder) in unused)
				{
					Log($"< {type}\\{folder}");
					Directory.Delete(Path.Combine(toWowDirectory(type), folder), true);
				}

				Log("Done.");
			}
			catch (Exception e)
			{
				Log($"ERROR: {e}");
			}

			Console.ReadKey(true);
		}

		private static async Task<IList<Addon>> GetAddons(string basePath)
		{
			var files = EnumerateAddonFiles(basePath);

			string ToRelative(string path) => path.Substring(basePath.Length + 1);

			var cachePath = Path.Combine(basePath, "addons.json");

			var cache = await DeserializeAsync<IEnumerable<CacheAddon>>(cachePath) ?? Enumerable.Empty<CacheAddon>();

			var changed = false;
			Addon ReadAddon(string path)
			{
				changed = true;
				Log($"Reading {ToRelative(path)}");
				return new(path);
			}

			var addons = files
				.GroupJoin(cache, path => ToRelative(path).ToLower(), x => x.Path.ToLower(), (path, matches) => matches.SingleOrDefault()?.ToAddon(basePath) ?? ReadAddon(path))
				.ToList();

			if (changed)
			{
				Serialize(cachePath, addons.Select(ToCache));
			}

			return addons;
		}

		private static string GetWowFolderTemplate() => @$"{GetWowBasePath()}\{{type}}\Interface\AddOns";

		private static readonly string[] possibleDrives = new[] { "C:\\", "D:\\" };
		private static readonly string[] possibleBasePaths = new[] { @"Program Files\World of Warcraft", @"Program Files (x86)\World of Warcraft" };

		private static string GetWowBasePath()
		{
			var installPath = (string)Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Blizzard Entertainment\World of Warcraft", "InstallPath", null);
			if (installPath != null && installPath.EndsWith(@"\_retail_\"))
			{
				return installPath.Substring(0, installPath.Length - 10);
			}

			installPath = possibleDrives.Join(possibleBasePaths, x => true, y => true, Path.Combine).FirstOrDefault(Directory.Exists);

			if (installPath != null)
			{
				return installPath;
			}

			throw new Exception("World of Warcraft install not found?");
		}

		private static IList<string> EnumerateAddonFiles(string addonDirectory) =>
			Directory.EnumerateFiles(addonDirectory, "*.zip", SearchOption.AllDirectories).Where(o => !Path.GetFileName(o).StartsWith("_")).ToList();

		private static string GetInstalledVersion(IEnumerable<string> directories, string wowDirectory) => CreateHashForFolder(directories.Select(path => Path.Combine(wowDirectory, path)).SelectMany(fullpath =>
			Directory.Exists(fullpath) ? Directory.EnumerateFiles(fullpath, "*", SearchOption.AllDirectories).Select(o => (o.Substring(wowDirectory.Length + 1), (Stream)File.OpenRead(o))) : Enumerable.Empty<(string, Stream)>()
		));

		class CacheAddon
		{
			public string Path { get; set; }
			public IReadOnlyCollection<string> Folders { get; set; }
			public string Version { get; set; }
		}

		class CacheFile
		{
			public string Hash { get; set; }
			public IEnumerable<CacheAddon> Data { get; set; }
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

			public Addon(string path, CacheAddon cache)
			{
				Archive = Path.Combine(path, cache.Path);
				Folders = cache.Folders;
				Version = cache.Version;
			}

			[JsonIgnore]
			public string Type { get { return Path.GetFileName(Path.GetDirectoryName(Archive)); } }

			public string Archive { get; }

			public IReadOnlyCollection<string> Folders { get; }

			public string Version { get; }
		}

		private static Addon ToAddon(this CacheAddon cache, string basePath) => new(basePath, cache);

		private static CacheAddon ToCache(Addon addon) => new()
		{
			Path = addon.Archive.Substring(Path.GetDirectoryName(Path.GetDirectoryName(addon.Archive)).Length+1),
			Folders = addon.Folders,
			Version = addon.Version
		};

		private readonly static JsonSerializerOptions JsonOptions = new()
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase
		};

		private static async Task<T> DeserializeAsync<T>(string path)
		{
			if (!File.Exists(path))
			{
				return default;
			}

			try
			{
				using var stream = File.OpenRead(path);
				return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions);
			}
			catch (JsonException)
			{
				return default;
			}
		}

		private static void Serialize<T>(string path, T obj) => File.WriteAllText(path, JsonSerializer.Serialize(obj, JsonOptions));

		private static string CreateHashForFolder(IEnumerable<(string file, Stream stream)> files)
		{
			var hash = SHA1.Create();

			int read;
			var buffer = new byte[4096];

			foreach (var (file, stream) in files.OrderBy(o => o.file))
			{
				// hash path
				hash.TransformString(file.ToLower());

				// hash contents
				using (stream)
				{
					while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
					{
						hash.TransformBlock(buffer, read);
					}
				}
			}

			hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

			return hash.GetHashString();
		}

		private static string GetBaseFolder(string path) => path.Split('/', '\\')[0];

		private static IEnumerable<(string type, string folder)> GetExistingAddons(string type, string wowDirectory)
		{
			if (!Directory.Exists(wowDirectory))
			{
				return Enumerable.Empty<(string, string)>();
			}

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

		private static T TransformString<T>(T hashAlgorithm, string text) where T : HashAlgorithm
		{
			hashAlgorithm.TransformBlock(Encoding.UTF8.GetBytes(text));
			return hashAlgorithm;
		}

		private static string GetFinalizedHashString<T>(T hashAlgorithm) where T : HashAlgorithm
		{
			hashAlgorithm.TransformFinalBlock();
			return hashAlgorithm.GetHashString();
		}

		public static int TransformString(this HashAlgorithm hashAlgorithm, string text) => hashAlgorithm.TransformBlock(Encoding.UTF8.GetBytes(text));
		public static int TransformBlock(this HashAlgorithm hashAlgorithm, byte[] block) => hashAlgorithm.TransformBlock(block, block.Length);
		public static int TransformBlock(this HashAlgorithm hashAlgorithm, byte[] block, int length) => hashAlgorithm.TransformBlock(block, 0, length, block, 0);
		public static byte[] TransformFinalBlock(this HashAlgorithm hashAlgorithm) => hashAlgorithm.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
		public static string GetHashString(this HashAlgorithm hashAlgorithm) => BitConverter.ToString(hashAlgorithm.Hash).Replace("-", "").ToLower();
	}
}
