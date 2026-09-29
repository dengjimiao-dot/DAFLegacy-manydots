namespace DFLegacy.Protocol;

/// <summary>
/// Decodes the rolling transform used by client-to-channel command payloads.
/// The client chooses a 15-bit initial seed. Its packet CRC covers the
/// sequence plus plaintext payload, allowing the local server to recover the
/// compatible seed without exchanging any additional key material.
/// </summary>
public sealed class GameClientCipherState
{
    private List<uint> _candidateSeeds = CreateInitialSeeds();

    public int CandidateCount => _candidateSeeds.Count;

    public bool TryDecode(PacketFrame request, out PacketFrame decoded)
    {
        decoded = request;
        if (request.Body.Length < sizeof(ushort) || _candidateSeeds.Count == 0)
        {
            return false;
        }

        var isCheckConnection = request.Type == GameProtocolEngine.CommandPacketType
            && request.ProtocolId == GameProtocolEngine.CheckConnectionCommand;

        // CHECK_CONNECTION is emitted through CNGameSocket's direct-send path
        // rather than the normal packet finalizer. Its body is plaintext and
        // it does not advance the per-session random seed.
        if (isCheckConnection && request.HasValidCrc32)
        {
            return true;
        }

        var payloadLength = request.Body.Length - sizeof(ushort);
        if (payloadLength == 0)
        {
            AdvanceEveryCandidate();
            return request.HasValidCrc32;
        }

        if (request.DeclaredCrc32 == 0)
        {
            return false;
        }

        var transformedBody = new byte[request.Body.Length];
        request.Body.AsSpan(0, sizeof(ushort)).CopyTo(transformedBody);
        byte[]? decodedBody = null;
        List<uint>? nextCandidates = null;
        var candidateSets = _candidateSeeds.Count == 0x8000
            ? [_candidateSeeds]
            : new[] { _candidateSeeds, CreateInitialSeeds() };
        foreach (var candidateSet in candidateSets)
        {
            var signatureResults = new Dictionary<int, (bool Matches, byte[]? Plaintext)>();
            var matchingCandidates = new List<uint>();
            foreach (var seed in candidateSet)
            {
                var nextSeed = seed;
                var randomValue = GameClientPayloadCipher.Advance(ref nextSeed);
                var key = (byte)randomValue;
                var rotation = (int)((randomValue >> 8) & 7);
                var signature = key | (rotation << 8);

                if (!signatureResults.TryGetValue(signature, out var result))
                {
                    GameClientPayloadCipher.Decrypt(
                        request.Body.AsSpan(sizeof(ushort)),
                        transformedBody.AsSpan(sizeof(ushort)),
                        key,
                        rotation);
                    var matches = Crc32.Compute(transformedBody) == request.DeclaredCrc32;
                    result = (matches, matches ? transformedBody.ToArray() : null);
                    signatureResults.Add(signature, result);
                }

                if (!result.Matches)
                {
                    continue;
                }

                matchingCandidates.Add(nextSeed);
                decodedBody ??= result.Plaintext;
            }

            if (decodedBody is null || matchingCandidates.Count == 0)
            {
                continue;
            }

            nextCandidates = matchingCandidates;
            break;
        }

        if (decodedBody is null || nextCandidates is null)
        {
            return false;
        }

        if (!isCheckConnection)
        {
            _candidateSeeds = nextCandidates;
        }

        decoded = new PacketFrame(request.Type, request.ProtocolId, request.DeclaredCrc32, decodedBody);
        return true;
    }

    private static List<uint> CreateInitialSeeds() => Enumerable.Range(0, 0x8000)
        .Select(value => (uint)value)
        .ToList();

    private void AdvanceEveryCandidate()
    {
        for (var index = 0; index < _candidateSeeds.Count; index++)
        {
            var seed = _candidateSeeds[index];
            GameClientPayloadCipher.Advance(ref seed);
            _candidateSeeds[index] = seed;
        }
    }
}

public static class GameClientPayloadCipher
{
    public static void Encrypt(Span<byte> payload, ref uint seed)
    {
        var randomValue = Advance(ref seed);
        var key = (byte)randomValue;
        var rotation = (int)((randomValue >> 8) & 7);
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = RotateRight((byte)(payload[index] ^ key), rotation);
        }
    }

    internal static uint Advance(ref uint seed)
    {
        var first = Next(seed);
        var second = Next(first);
        var third = Next(second);
        seed = third;

        var high11 = (first >> 16) & 0x7ff;
        var high10 = (second >> 16) & 0x3ff;
        var final10 = (third >> 16) & 0x3ff;
        return final10 ^ ((high10 ^ (high11 << 10)) << 10);
    }

    internal static void Decrypt(
        ReadOnlySpan<byte> ciphertext,
        Span<byte> plaintext,
        byte key,
        int rotation)
    {
        for (var index = 0; index < ciphertext.Length; index++)
        {
            plaintext[index] = (byte)(RotateLeft(ciphertext[index], rotation) ^ key);
        }
    }

    private static uint Next(uint value) => unchecked(value * 0x41C64E6D + 0x3039);

    private static byte RotateLeft(byte value, int count) => count == 0
        ? value
        : (byte)((value << count) | (value >> (8 - count)));

    private static byte RotateRight(byte value, int count) => count == 0
        ? value
        : (byte)((value >> count) | (value << (8 - count)));
}
