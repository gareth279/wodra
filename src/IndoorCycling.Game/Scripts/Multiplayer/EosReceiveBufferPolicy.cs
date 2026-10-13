using System;

/// <summary>Bounds the managed receive buffer and EOS MaxDataSizeBytes consistently.</summary>
public readonly record struct EosReceiveBufferPlan(int BufferLength, uint MaxDataSizeBytes, bool Oversized)
{
	public static EosReceiveBufferPlan ForPacket(uint packetSizeBytes, int maximumMessageBytes)
	{
		if (maximumMessageBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumMessageBytes));
		var maxBytes = checked((uint)maximumMessageBytes);
		var oversized = packetSizeBytes > maxBytes;
		var receiveBytes = packetSizeBytes == 0 || oversized ? maxBytes : packetSizeBytes;
		return new EosReceiveBufferPlan(checked((int)receiveBytes), receiveBytes, oversized);
	}
}
