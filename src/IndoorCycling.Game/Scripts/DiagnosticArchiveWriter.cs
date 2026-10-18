using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

public static class DiagnosticArchiveWriter
{
	public static void WriteStreaming(string path, IEnumerable<(string Name, string SourcePath, long Length)> files, long maximumUncompressedBytes)
	{
		var entries = files.ToArray();
		if (entries.Any(entry => Path.IsPathRooted(entry.Name) || entry.Name.Split('/', '\\').Any(part => part == "..")))
			throw new InvalidDataException("Diagnostic archive entry has an unsafe path.");
		if (entries.Sum(entry => entry.Length) > maximumUncompressedBytes)
			throw new InvalidDataException("Diagnostic archive exceeds its size limit.");
		try
		{
			using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
				foreach (var file in entries)
				{
					var entry = archive.CreateEntry(file.Name, CompressionLevel.Optimal);
					using var target = entry.Open();
					using var source = new FileStream(file.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
					CopyExactly(source, target, file.Length);
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

	private static void CopyExactly(Stream source, Stream target, long length)
	{
		var buffer = new byte[64 * 1024];
		var remaining = length;
		while (remaining > 0)
		{
			var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
			if (read == 0) throw new EndOfStreamException("A diagnostic file changed while it was being archived.");
			target.Write(buffer, 0, read);
			remaining -= read;
		}
	}

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
