namespace VysorClient.Services;

// Faxina do que a presença no Discord deixou pra trás.
//
// POR QUE ISSO EXISTE (e por que é pequeno de propósito)
// Até a 1.6.x o Vysor publicava um status no Discord com um botão "Entrar".
// Pra isso funcionar, ele precisava registrar no Windows COMO o Discord deveria
// abrir o Vysor — uma entrada no registro do usuário, gravada a cada abertura
// do app.
//
// Esse recurso foi removido: o aviso no canal, feito pelo bot do lado do
// servidor, resolve melhor o mesmo problema e chega em todo mundo (a presença
// só funcionava direito pra conta dona do aplicativo).
//
// Mas quem usou aquelas versões TEM a entrada gravada na máquina, apontando pro
// Vysor, de um recurso que não existe mais. Desinstalar o app não a remove, e
// nada mais vai limpá-la — então limpamos aqui, uma vez, em silêncio. Deixar
// lixo de configuração no computador de alguém depois de remover a
// funcionalidade é falta de cuidado.
public static class DiscordLegacy
{
    // O identificador do aplicativo no Discord que a presença usava. Fica
    // escrito aqui porque é justamente o que precisamos APAGAR — não há mais
    // nenhum outro lugar no app que o conheça.
    private const string PresenceKey = @"Software\Classes\discord-1543678716817842326";

    public static void ForgetPresenceRegistration()
    {
        try
        {
            using var classes = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Classes", writable: true);

            // DeleteSubKeyTree com throwOnMissingSubKey: false — na esmagadora
            // maioria dos computadores a chave nunca existiu, e isso é o caso
            // normal, não um erro.
            classes?.DeleteSubKeyTree("discord-1543678716817842326", throwOnMissingSubKey: false);
        }
        catch
        {
            // Perfil sem permissão de escrita, política de empresa: a entrada
            // fica lá, inofensiva. Nunca vale atrapalhar a abertura do app por
            // causa de uma faxina.
        }
    }
}
