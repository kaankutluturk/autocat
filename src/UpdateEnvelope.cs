using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

internal sealed class ReleaseManifest {
    internal readonly long Sequence, PayloadBytes;
    internal readonly Version Version;
    internal readonly string RuntimeId, Sha256, PayloadUrl;
    internal readonly bool IsNewer;
    internal ReleaseManifest(long sequence, Version version, string runtime, long bytes,
            string hash, bool newer) {
        Sequence = sequence; Version = version; RuntimeId = runtime; PayloadBytes = bytes;
        Sha256 = hash; PayloadUrl = UpdateEnvelope.PayloadFor(version); IsNewer = newer;
    }
}

// Verifies the exact signed manifest bytes.
internal static class UpdateEnvelope {
    internal const int MaximumManifestBytes = 4096;
    internal const long MaximumPayloadBytes = 20 * 1024 * 1024;
    // Each release is its own vMAJOR.MINOR.PATCH GitHub release with autocat.exe as its only asset. The signed
    // manifest must name exactly this address for its own version, so it cannot point anywhere else.
    internal const string PayloadPrefix = "https://github.com/kaankutluturk/autocat/releases/download/v";
    internal static string PayloadFor(Version version) {
        return PayloadPrefix + version.Major + "." + version.Minor + "." + version.Build + "/autocat.exe";
    }
    static readonly string[] Names = {"schema", "channel", "sequence", "version", "runtime", "bytes", "sha256", "payload"};

    internal static ReleaseManifest Verify(byte[] manifest, byte[] signature, string publicKeyXml,
            long lastSequence, Version runningVersion) {
        if (manifest == null || manifest.Length == 0 || manifest.Length > MaximumManifestBytes ||
                signature == null || signature.Length != 384 || String.IsNullOrEmpty(publicKeyXml) ||
                lastSequence < 0 || runningVersion == null)throw new InvalidDataException("Invalid update envelope.");
        // Authenticate before parsing.
        using (var rsa = new RSACryptoServiceProvider(new CspParameters(24))) {
            rsa.PersistKeyInCsp = false;
            rsa.FromXmlString(publicKeyXml);
            if (rsa.KeySize != 3072 || !rsa.VerifyData(manifest, "SHA256", signature))
                throw new CryptographicException("Update signature is not trusted.");
        }
        foreach (byte value in manifest)
            if (value != 10 && (value < 32 || value > 126))throw new InvalidDataException("Manifest must be ASCII with LF lines.");
        string source = Encoding.ASCII.GetString(manifest);
        string[] lines = source.Split('\n');
        if (lines.Length != Names.Length + 1 || lines[Names.Length] != "")throw new InvalidDataException("Invalid manifest fields.");
        var values = new string[Names.Length];
        for (int i = 0; i < Names.Length; i++) {
            string prefix = Names[i] + "=";
            if (!lines[i].StartsWith(prefix, StringComparison.Ordinal))throw new InvalidDataException("Invalid manifest field order.");
            values[i] = lines[i].Substring(prefix.Length);
        }
        if (values[0] != "1" || values[1] != "stable")
            throw new InvalidDataException("Unsupported update schema or channel.");
        long sequence = Number(values[2]), bytes = Number(values[5]);
        if (sequence <= 0 || sequence < lastSequence || bytes <= 0 || bytes > MaximumPayloadBytes)
            throw new InvalidDataException("Update sequence or payload size is invalid.");
        var parts = values[3].Split('.');
        if (parts.Length != 3)throw new InvalidDataException("Invalid release version.");
        var digits = new int[3];
        for (int i = 0; i < 3; i++) {
            long number = Number(parts[i]);
            if (number > 65535)throw new InvalidDataException("Version component is too large.");
            digits[i] = (int)number;
        }
        var version = new Version(digits[0], digits[1], digits[2]);
        if (values[7] != PayloadFor(version))throw new InvalidDataException("Unsupported update address.");
        var current = new Version(runningVersion.Major, runningVersion.Minor, Math.Max(0, runningVersion.Build));
        if (version < current)
            throw new InvalidDataException("Update replay or downgrade refused.");
        if (values[4] != String.Concat(parts))throw new InvalidDataException("Runtime identity does not match release version.");
        if (values[6].Length != 64)throw new InvalidDataException("Invalid payload digest.");
        foreach (char value in values[6])
            if (!(value >= '0' && value <= '9') && !(value >= 'A' && value <= 'F'))throw new InvalidDataException("Invalid payload digest.");
        return new ReleaseManifest(sequence, version, values[4], bytes, values[6], version > current);
    }

    static long Number(string value) {
        long result;
        if (value.Length == 0 || (value.Length > 1 && value[0] == '0') ||
                !Int64.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result))
            throw new InvalidDataException("Invalid manifest integer.");
        return result;
    }

    internal static void VerifyPayload(ReleaseManifest manifest, string payloadPath) {
        if (manifest == null || manifest.PayloadBytes <= 0 || manifest.PayloadBytes > MaximumPayloadBytes)
            throw new InvalidDataException("Invalid payload metadata.");
        using (var input = new FileStream(payloadPath, FileMode.Open, FileAccess.Read, FileShare.Read)) {
            if (input.Length != manifest.PayloadBytes)throw new InvalidDataException("Payload size does not match signed release.");
            using (var hash = SHA256.Create()) {
                string actual = BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "");
                if (!String.Equals(actual, manifest.Sha256, StringComparison.Ordinal))
                    throw new InvalidDataException("Payload digest does not match signed release.");
            }
        }
    }
}
