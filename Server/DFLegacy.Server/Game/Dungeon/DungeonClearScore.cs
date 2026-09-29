using System.Buffers.Binary;

namespace DFLegacy.Server;

public sealed record DungeonClearScore(
    ushort[] StyleComponents,
    ushort[] TechniqueComponents,
    int HitPenalty,
    ushort Style,
    ushort Technique,
    byte WireResultCode)
{
    public int CalculatedScore => Math.Clamp(
        (int)Style + Technique + HitPenalty,
        0,
        ushort.MaxValue);

    public byte CalculatedResultCode => ResultCodeFromScore(CalculatedScore);

    public bool IsWireResultCodeConsistent =>
        WireResultCode == CalculatedResultCode;

    public static bool TryParse(ReadOnlySpan<byte> body, out DungeonClearScore score)
    {
        score = null!;
        if (body.Length < 24)
        {
            return false;
        }

        var rawResultCode = BinaryPrimitives.ReadUInt16LittleEndian(body[22..24]);
        if (rawResultCode > byte.MaxValue)
        {
            return false;
        }

        score = new DungeonClearScore(
            [
                BinaryPrimitives.ReadUInt16LittleEndian(body[2..4]),
                BinaryPrimitives.ReadUInt16LittleEndian(body[4..6]),
                BinaryPrimitives.ReadUInt16LittleEndian(body[6..8])
            ],
            [
                BinaryPrimitives.ReadUInt16LittleEndian(body[8..10]),
                BinaryPrimitives.ReadUInt16LittleEndian(body[10..12]),
                BinaryPrimitives.ReadUInt16LittleEndian(body[12..14])
            ],
            BinaryPrimitives.ReadInt32LittleEndian(body[14..18]),
            BinaryPrimitives.ReadUInt16LittleEndian(body[18..20]),
            BinaryPrimitives.ReadUInt16LittleEndian(body[20..22]),
            checked((byte)rawResultCode));
        return true;
    }

    public static byte ResultCodeFromScore(int score)
    {
        foreach (var threshold in ResultCodeThresholds)
        {
            if (score >= threshold)
            {
                return threshold;
            }
        }

        return 0;
    }

    public static bool IsValidResultCode(byte resultCode) =>
        resultCode == 0 || ResultCodeThresholds.Contains(resultCode);

    private static readonly byte[] ResultCodeThresholds =
        [105, 95, 85, 70, 55, 45, 40, 35];
}
