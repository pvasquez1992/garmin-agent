using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GarminAgent;

// These values are used only by the browser login code, never by the agent/model.
public sealed class GarminCredentials
{
    [JsonPropertyName("version")] public int Version { get; init; }
    [JsonPropertyName("email")] public string Email { get; init; } = "";
    [JsonPropertyName("password")] public string Password { get; init; } = "";

    public static string FilePath(string root) => Path.Combine(root, ".local", "garmin-credentials.bin");
    public static bool Exists(string root) => File.Exists(FilePath(root));

    public static GarminCredentials Read(string root)
    {
        byte[]? plain = null;
        try
        {
            var file = FilePath(root);
            if (!File.Exists(file) || new FileInfo(file).Length > 64 * 1024)
                throw new AgentException("Configura el acceso con scripts/set-garmin-credentials.ps1.");
            plain = ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser);
            var credentials = JsonSerializer.Deserialize<GarminCredentials>(plain);
            if (credentials is null || credentials.Version != 1
                || string.IsNullOrWhiteSpace(credentials.Email) || credentials.Email.Length > 320
                || string.IsNullOrEmpty(credentials.Password) || credentials.Password.Length > 4096)
                throw new AgentException("El archivo de credenciales no tiene el formato esperado. Vuelve a configurarlo.");
            return credentials;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {
            throw new AgentException("No se pudieron abrir las credenciales cifradas. Vuelve a configurarlas con este usuario de Windows.");
        }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }
}
