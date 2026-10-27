using Godot;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>Profile-scoped companion identity protected by the current Windows user DPAPI.</summary>
internal static class CompanionDesktopIdentityStore
{
	internal sealed record Identity(string DesktopId, string Secret);

	public static Identity LoadOrCreate(Guid? profileId)
	{
		if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Remembered companion identity requires Windows DPAPI.");
		var directory = ProjectSettings.GlobalizePath("user://settings/companion");
		Directory.CreateDirectory(directory);
		var scope = profileId?.ToString("N") ?? "installation";
		var path = Path.Combine(directory, $"{scope}.identity");
		if (File.Exists(path))
		{
			var protectedBytes = File.ReadAllBytes(path);
			var clear = Dpapi.Unprotect(protectedBytes);
			try
			{
				var identity = JsonSerializer.Deserialize<Identity>(clear);
				if (identity is { DesktopId.Length: >= 32, Secret.Length: >= 40 }) return identity;
			}
			finally { CryptographicOperations.ZeroMemory(clear); }
			throw new InvalidDataException("Stored companion identity is invalid.");
		}

		var created = new Identity(Guid.NewGuid().ToString("N"), NewSecret());
		var clearBytes = JsonSerializer.SerializeToUtf8Bytes(created);
		var encrypted = Dpapi.Protect(clearBytes);
		CryptographicOperations.ZeroMemory(clearBytes);
		var temporary = path + ".tmp";
		File.WriteAllBytes(temporary, encrypted);
		File.Move(temporary, path, overwrite: false);
		return created;
	}

	private static string NewSecret()
	{
		var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
		return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
	}

	private static class Dpapi
	{
		[StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int Length; public IntPtr Data; }
		[DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
		[return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref DataBlob input, string? description,
			IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
		[DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
		[return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref DataBlob input, out IntPtr description,
			IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
		[DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);

		public static byte[] Protect(byte[] value) => Transform(value, protect: true);
		public static byte[] Unprotect(byte[] value) => Transform(value, protect: false);

		private static byte[] Transform(byte[] value, bool protect)
		{
			var inputMemory = Marshal.AllocHGlobal(value.Length);
			var input = new DataBlob { Length = value.Length, Data = inputMemory };
			Marshal.Copy(value, 0, inputMemory, value.Length);
			DataBlob output = default;
			IntPtr description = IntPtr.Zero;
			try
			{
				var succeeded = protect
					? CryptProtectData(ref input, "Wodra companion profile identity", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
					: CryptUnprotectData(ref input, out description, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
				if (!succeeded) throw new CryptographicException(Marshal.GetLastWin32Error());
				var result = new byte[output.Length];
				Marshal.Copy(output.Data, result, 0, result.Length);
				return result;
			}
			finally
			{
				if (inputMemory != IntPtr.Zero && input.Length > 0)
					Marshal.Copy(new byte[input.Length], 0, inputMemory, input.Length);
				Marshal.FreeHGlobal(inputMemory);
				if (output.Data != IntPtr.Zero)
				{
					if (output.Length > 0) Marshal.Copy(new byte[output.Length], 0, output.Data, output.Length);
					LocalFree(output.Data);
				}
				if (description != IntPtr.Zero) LocalFree(description);
			}
		}
	}
}
