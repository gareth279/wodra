using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

public static class DiagnosticArchiveWriter
{
	public static void Write(string path, IEnumerable<(string Name, byte[] Data)> files, long maximumUncompressedBytes)
	{
		var entries = files.ToArray();
		if (entries.Any(entry => Path.IsPathRooted(entry.Name) || entry.Name.Split('/', '\\').Any(part => part == "..")))
			throw new InvalidDataException("Diagnostic archive entry has an unsafe path.");
		if (entries.Sum(entry => (long)entry.Data.LongLength) > maximumUncompressedBytes)
			throw new InvalidDataException("Diagnostic archive exceeds its size limit.");
		try
		{
			using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
				foreach (var file in entries)
				{
					var entry = archive.CreateEntry(file.Name, CompressionLevel.Optimal);
					using var target = entry.Open(); target.Write(file.Data);
				}
			if (new FileInfo(path).Length > maximumUncompressedBytes + 256 * 1024)
				throw new InvalidDataException("Diagnostic archive exceeds its size limit.");
		}
		catch
		{
			try { File.Delete(path); } catch { }
			throw;
		}
	}
}
