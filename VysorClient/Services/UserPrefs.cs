using System.IO;

namespace VysorClient.Services;

// Guarda o pouquinho que precisa sobreviver ao fechar do app.
//
// POR QUE APARECEU AGORA
// Por causa do convite clicável. Quando alguém clica num "vysor://T2X9RF" com
// o Vysor FECHADO, o app abre do zero — e, sem nome salvo, ele não tem como
// entrar na sala sozinho: pararia na tela inicial pedindo pra digitar o nome,
// que é exatamente o passo manual que o link existia pra eliminar. Salvar o
// nome é o que faz a diferença entre "cliquei e entrei" e "cliquei e o app
// abriu".
//
// É um arquivo de texto simples de propósito: são dados sem valor nenhum pra
// mais ninguém, e um formato que a própria pessoa consegue abrir e apagar vale
// mais aqui do que um banco de dados.
public static class UserPrefs
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Vysor");

    private static readonly string NameFile = Path.Combine(Folder, "nome.txt");

    // Nunca lança: preferência é conveniência, e conveniência não pode
    // impedir o app de abrir. Se o disco estiver cheio, sem permissão, ou o
    // perfil for móvel e indisponível, a pessoa só digita o nome de novo.
    public static string? LoadDisplayName()
    {
        try
        {
            if (!File.Exists(NameFile)) return null;
            string name = File.ReadAllText(NameFile).Trim();
            return name.Length is > 0 and <= 60 ? name : null;
        }
        catch
        {
            return null;
        }
    }

    // Apaga o arquivo de webhook que versões anteriores gravavam. Quem usou a
    // 1.4.x tem um endereço de webhook guardado no perfil, e ele não serve
    // mais pra nada — é só um segredo esquecido no disco. Limpar na primeira
    // abertura é mais correto do que deixar apodrecendo lá.
    public static void ForgetLegacyWebhook()
    {
        try
        {
            string old = Path.Combine(Folder, "discord-webhook.txt");
            if (File.Exists(old)) File.Delete(old);
        }
        catch { }
    }

    private static readonly string ChannelFile = Path.Combine(Folder, "discord-canal.txt");

    // Canal do Discord escolhido por ESTA pessoa (id e nome, uma linha cada).
    //
    // POR QUE FICA AQUI E NÃO NO SERVIDOR
    // O servidor roda no plano grátis do Render, que APAGA O DISCO a cada
    // reinício — e ele reinicia sozinho quando dorme. Uma tabela
    // "pessoa → canal" lá se perderia toda hora, e a pessoa teria que
    // reconfigurar sem entender por quê. Aqui sobrevive a tudo, e não exige
    // banco de dados nenhum.
    //
    // O segredo continua só no servidor: isto é apenas um número de canal, que
    // não dá poder nenhum a quem o tenha. Quem POSTA é o servidor.
    public static (string Id, string Name)? LoadChannel()
    {
        try
        {
            if (!File.Exists(ChannelFile)) return null;
            string[] lines = File.ReadAllLines(ChannelFile);
            if (lines.Length == 0) return null;

            string id = lines[0].Trim();
            if (id.Length == 0) return null;

            string name = lines.Length > 1 ? lines[1].Trim() : "canal escolhido";
            return (id, name.Length > 0 ? name : "canal escolhido");
        }
        catch
        {
            return null;
        }
    }

    public static void SaveChannel(string? id, string? name)
    {
        try
        {
            Directory.CreateDirectory(Folder);

            id = (id ?? string.Empty).Trim();
            if (id.Length == 0) { if (File.Exists(ChannelFile)) File.Delete(ChannelFile); return; }

            File.WriteAllLines(ChannelFile, new[] { id, (name ?? string.Empty).Trim() });
        }
        catch { }
    }

    private static readonly string AnnounceOffFile = Path.Combine(Folder, "discord-avisar-nao.txt");

    // O aviso no Discord vem LIGADO (o app já sabe o canal do grupo). Por isso
    // o que se guarda é a EXCEÇÃO — quem desligou — e não o contrário: assim
    // quem nunca mexeu em nada recebe o comportamento novo sem precisar fazer
    // coisa alguma, que é o ponto de já vir configurado.
    public static bool LoadAnnounceEnabled()
    {
        try { return !File.Exists(AnnounceOffFile); } catch { return true; }
    }

    public static void SaveAnnounceEnabled(bool enabled)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            if (enabled) { if (File.Exists(AnnounceOffFile)) File.Delete(AnnounceOffFile); }
            else File.WriteAllText(AnnounceOffFile, "1");
        }
        catch { }
    }

    public static void SaveDisplayName(string? name)
    {
        try
        {
            name = (name ?? string.Empty).Trim();
            if (name.Length == 0 || name.Length > 60) return;

            Directory.CreateDirectory(Folder);
            File.WriteAllText(NameFile, name);
        }
        catch
        {
            // Idem: falhar em salvar não pode atrapalhar nada.
        }
    }
}
