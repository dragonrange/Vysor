using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace VysorClient.Services;

public sealed record UpdateInfo(Version Version, string DownloadUrl, string ReleaseUrl);

// Confere no GitHub se existe uma versão do Vysor mais nova que a que está
// rodando, e devolve de onde baixar o instalador se existir.
//
// Por que GitHub Releases em vez de um servidor próprio: é gratuito, o
// código já mora lá, e não precisa de mais nada rodando 24/7 só pra
// responder "qual é a versão mais nova" — o mesmo raciocínio que tirou o
// relay de vídeo do servidor (ver RoomHub.cs) vale aqui: quanto menos coisa
// dependendo de um serviço que pode ficar fora do ar ou custar dinheiro,
// melhor.
//
// COMO PUBLICAR UMA ATUALIZAÇÃO (o "servidor" daqui é você, na mão):
//   1. Suba o Vysor.iss com o número de versão novo (mesmo valor do
//      <Version> no VysorClient.csproj).
//   2. Gere o instalador (ver GERAR_EXE.md) — o arquivo final TEM que se
//      chamar exatamente "VysorSetup.exe" (é esse nome que o app procura).
//   3. No GitHub: Releases → Draft a new release → crie uma tag "vX.Y.Z"
//      (com o "v" na frente) → anexe o VysorSetup.exe como asset → Publish.
//   4. Pronto. Quem já tem o Vysor aberto vê o aviso de atualização na
//      próxima vez que abrir o app (ou na hora, se você adicionar uma
//      checagem periódica — hoje só confere ao abrir).
public static class UpdateChecker
{
    private const string ReleasesApiUrl =
        "https://api.github.com/repos/dragonrange/Vysor/releases/latest";

    // Nome exato do arquivo que precisa estar anexado à Release (Assets).
    // Se não bater, o app não acha o instalador e simplesmente não oferece
    // a atualização — nunca trava por causa disso.
    private const string InstallerAssetName = "VysorSetup.exe";

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    public static async Task<UpdateInfo?> CheckAsync()
    {
        try
        {
            using var http = BuildClient();
            using var response = await http.GetAsync(ReleasesApiUrl);

            // 404 é o caso normal de "ainda não existe nenhuma release" —
            // não é erro, só não tem nada pra oferecer. Qualquer outro
            // código ruim (GitHub fora do ar, limite de taxa) também só
            // significa "sem checagem agora", nunca quebra o app.
            if (!response.IsSuccessStatusCode) return null;

            using var stream = await response.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(stream);
            var root = doc.RootElement;

            string? tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag)) return null;

            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var remoteVersion)) return null;

            // Compara só major.minor.build: a versão que vem do assembly
            // costuma ganhar um quarto número (revision) que a tag do
            // GitHub não tem, e comparar os dois direto faria uma versão
            // IGUAL parecer mais velha (System.Version trata "revisão não
            // informada" como -1, que é menor que 0).
            if (Trim(remoteVersion) <= Trim(CurrentVersion)) return null;

            if (!root.TryGetProperty("assets", out var assets)) return null;

            string? downloadUrl = null;
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.TryGetProperty("name", out var nameEl)
                    && nameEl.GetString() == InstallerAssetName
                    && asset.TryGetProperty("browser_download_url", out var urlEl))
                {
                    downloadUrl = urlEl.GetString();
                    break;
                }
            }
            if (downloadUrl == null) return null;   // release existe, mas ninguém anexou o instalador ainda

            string releaseUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
            return new UpdateInfo(remoteVersion, downloadUrl, releaseUrl);
        }
        catch
        {
            // Sem internet, GitHub fora do ar, resposta em formato
            // inesperado: a checagem de atualização nunca pode ser o motivo
            // do app não abrir. Só tenta de novo na próxima vez.
            return null;
        }
    }

    // Baixa o instalador pra pasta temporária do Windows e devolve o
    // caminho. "progress" (0.0 a 1.0) é opcional, pra quem quiser mostrar
    // uma barra — sem ele, o download acontece do mesmo jeito.
    public static async Task<string> DownloadInstallerAsync(string url, IProgress<double>? progress = null)
    {
        string path = Path.Combine(Path.GetTempPath(), InstallerAssetName);

        using var http = BuildClient();
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;
        await using var httpStream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = File.Create(path);

        var buffer = new byte[81920];
        long readTotal = 0;
        int read;
        while ((read = await httpStream.ReadAsync(buffer)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read));
            readTotal += read;
            if (total is > 0) progress?.Report((double)readTotal / total.Value);
        }

        return path;
    }

    private static HttpClient BuildClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // A API do GitHub exige um User-Agent identificando quem pergunta —
        // sem isso ela recusa a chamada com 403.
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Vysor", CurrentVersion.ToString()));
        return http;
    }

    private static Version Trim(Version v) =>
        new(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
}
