namespace VysorServer;

// As páginas públicas do Vysor: termos, privacidade e a moldura visual comum.
//
// POR QUE ELAS EXISTEM
// O Discord exige um link de termos e um de política de privacidade pra
// verificar um aplicativo. Poderiam ser dois textos genéricos copiados da
// internet — e seriam inúteis, porque não descreveriam este programa. Um texto
// de privacidade que não corresponde ao que o software faz é pior do que
// nenhum: dá garantia falsa a quem lê.
//
// Então o que está aqui é o que o Vysor REALMENTE faz, incluindo a parte que
// mais importa e que quase nenhum app desse tipo pode dizer: a imagem e o som
// não passam por servidor nenhum.
public static class LegalPages
{
    public static string Shell(string title, string body) => $$"""
<!doctype html>
<html lang="pt-BR"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>{{title}}</title>
<style>
 body{background:#0d0d10;color:#d1d5db;font-family:Segoe UI,system-ui,sans-serif;
      margin:0;padding:40px 20px;line-height:1.65}
 .wrap{max-width:680px;margin:0 auto}
 h1,h2{color:#fff} h1{font-size:26px} h2{font-size:19px;margin-top:34px}
 a{color:#8b93f8}
 code{background:#1e1e24;padding:2px 6px;border-radius:4px;font-size:13px}
 .small{color:#6b7280;font-size:13px;margin-top:34px}
 .list{display:flex;flex-wrap:wrap;gap:10px;margin:22px 0}
 a.ch{display:inline-block;background:#5865f2;color:#fff;text-decoration:none;
      padding:11px 18px;border-radius:8px;font-weight:600}
 .logo{color:#5865f2;font-weight:700;letter-spacing:1px;margin-bottom:6px}
</style></head>
<body><div class="wrap">
<div class="logo">⚡ VYSOR</div>
{{body}}
<p class="small">Vysor — app de compartilhamento de tela entre amigos.
<a href="https://github.com/dragonrange/Vysor">Código-fonte no GitHub</a>.</p>
</div></body></html>
""";

    public static string Terms => Shell("Termos de Serviço — Vysor", """
<h1>Termos de Serviço</h1>
<p>O Vysor é um programa gratuito para compartilhar a tela entre amigos. Não há
   cobrança, assinatura, conta de usuário nem cadastro.</p>

<h2>O que o Vysor faz</h2>
<p>Permite que pessoas convidadas para uma sala vejam a tela e ouçam o áudio de
   quem estiver transmitindo. A imagem e o som viajam <b>diretamente de um
   computador para o outro</b>, criptografados. O servidor apenas apresenta os
   participantes uns aos outros; ele não é capaz de transportar vídeo ou áudio.</p>

<h2>Uso aceitável</h2>
<p>Você é responsável pelo que transmite. Não use o Vysor para compartilhar
   conteúdo ilegal, para gravar ou expor alguém sem consentimento, nem para
   qualquer finalidade que viole a lei ou os direitos de terceiros.</p>
<p>Você só deve entrar em uma sala se foi convidado. O código de sala não é
   uma credencial de segurança forte.</p>

<h2>Integração com o Discord</h2>
<p>Se o Vysor for adicionado a um servidor do Discord, ele publica uma mensagem
   no canal escolhido pelo administrador quando alguém abre uma sala. Essa
   mensagem contém o nome de exibição de quem abriu e o código da sala. O Vysor
   não lê conversas, não acessa membros e não apaga nada.</p>
<p>O administrador pode remover o aplicativo do servidor a qualquer momento,
   pelas configurações do próprio Discord, e a publicação cessa imediatamente.</p>

<h2>Sem garantias</h2>
<p>O Vysor é oferecido "como está", sem garantia de funcionamento contínuo,
   disponibilidade ou adequação a qualquer finalidade. É um projeto pessoal,
   mantido sem obrigação de suporte. O serviço pode ser descontinuado a
   qualquer momento.</p>

<h2>Contato</h2>
<p>Dúvidas, problemas ou pedidos de remoção:
   <a href="https://github.com/dragonrange/Vysor/issues">abra uma questão no GitHub</a>.</p>
""");

    public static string Privacy => Shell("Política de Privacidade — Vysor", """
<h1>Política de Privacidade</h1>
<p>Resumo honesto: o Vysor não tem contas, não cria perfis, não usa rastreadores
   e não vende nem compartilha dados com ninguém. O que ele guarda é o mínimo
   para uma sala funcionar, e some sozinho.</p>

<h2>A sua tela e o seu áudio não passam pelo servidor</h2>
<p>Esta é a parte mais importante. Vídeo e áudio vão <b>direto do seu computador
   para o de quem assiste</b>, criptografados de ponta a ponta com uma chave
   derivada do código da sala. O servidor não recebe, não armazena e não tem
   como interceptar essa transmissão — ele sequer possui um mecanismo capaz de
   transportá-la.</p>

<h2>O que o servidor recebe</h2>
<ul>
  <li><b>Código da sala</b> — gerado aleatoriamente pelo próprio servidor.</li>
  <li><b>Nome de exibição</b> — o que você digita no app. Pode ser qualquer coisa;
      não é verificado nem associado a você.</li>
  <li><b>Endereços de rede</b> (IP e porta) — necessários para que os computadores
      se encontrem diretamente. É o mesmo tipo de informação que qualquer
      chamada de vídeo troca, e existe apenas para estabelecer a conexão.</li>
</ul>

<h2>Por quanto tempo</h2>
<p>Tudo isso fica apenas na memória do servidor, enquanto a sala existe. Quando
   a sala esvazia, é descartada após alguns minutos de tolerância — e, se o
   servidor reinicia, some imediatamente. <b>Não há banco de dados</b> e nada é
   gravado em disco.</p>

<h2>Discord</h2>
<p>Se um administrador adicionou o Vysor ao servidor dele no Discord, ao abrir
   uma sala o Vysor publica no canal escolhido uma mensagem com o seu nome de
   exibição e o código da sala. Essa mensagem fica no Discord e segue as
   políticas dele.</p>
<p>O Vysor não lê mensagens, não acessa lista de membros, não coleta dados de
   ninguém do servidor e não guarda qualquer informação vinda do Discord.</p>

<h2>Menores de idade</h2>
<p>O Vysor não é direcionado a crianças e não coleta intencionalmente dados de
   menores de 13 anos.</p>

<h2>Seus dados</h2>
<p>Como nada é armazenado de forma persistente, não há histórico para consultar
   ou apagar: sair da sala e fechar o app já elimina tudo que existia. Para
   remover uma mensagem publicada no Discord, apague-a pelo próprio Discord.</p>

<h2>Contato</h2>
<p><a href="https://github.com/dragonrange/Vysor/issues">Abra uma questão no GitHub</a>.</p>
""");
}
