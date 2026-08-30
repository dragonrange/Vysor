# Política de Privacidade — Vysor

*Também publicada em https://vysorserver-cjxi.onrender.com/privacidade (o
servidor é de plano gratuito e pode levar até um minuto para responder após um
período ocioso; esta cópia no repositório é sempre imediata).*

Resumo honesto: o Vysor não tem contas, não cria perfis, não usa rastreadores e
não vende nem compartilha dados com ninguém. O que ele guarda é o mínimo para
uma sala funcionar, e some sozinho.

## A sua tela e o seu áudio não passam pelo servidor

Esta é a parte mais importante. Vídeo e áudio vão **direto do seu computador
para o de quem assiste**, criptografados de ponta a ponta (AES-256-GCM) com uma
chave derivada do código da sala.

O servidor não recebe, não armazena e não tem como interceptar essa transmissão
— ele sequer possui um mecanismo capaz de transportá-la. Isso é verificável no
código: veja `VysorServer/Hubs/RoomHub.cs`, que não expõe nenhum método capaz de
receber ou repassar mídia.

## O que o servidor recebe

- **Código da sala** — gerado aleatoriamente pelo próprio servidor.
- **Nome de exibição** — o que você digita no app. Pode ser qualquer coisa; não
  é verificado nem associado a você.
- **Endereços de rede** (IP e porta) — necessários para que os computadores se
  encontrem diretamente. É o mesmo tipo de informação que qualquer chamada de
  vídeo troca, e existe apenas para estabelecer a conexão.

## Por quanto tempo

Tudo isso fica apenas na memória do servidor, enquanto a sala existe. Quando a
sala esvazia, é descartada após alguns minutos de tolerância — e, se o servidor
reinicia, some imediatamente. **Não há banco de dados** e nada é gravado em
disco.

## Integração com o Discord

Se um administrador adicionou o Vysor ao servidor dele no Discord, ao abrir uma
sala o Vysor publica no canal escolhido uma mensagem com o seu nome de exibição
e o código da sala. Essa mensagem fica no Discord e segue as políticas dele.

O Vysor não lê mensagens, não acessa lista de membros, não coleta dados de
ninguém do servidor e não guarda qualquer informação vinda do Discord.

Ao usar "Conectar meu Discord", a lista dos seus servidores é comparada com a
lista dos servidores onde o Vysor está instalado **dentro do seu navegador**. O
servidor do Vysor nunca vê de quais servidores você participa.

## Dados guardados no seu computador

Em `%APPDATA%\Vysor`, apenas: o nome que você escolheu e o canal do Discord
selecionado. São arquivos de texto simples, que você pode abrir ou apagar.

## Menores de idade

O Vysor não é direcionado a crianças e não coleta intencionalmente dados de
menores de 13 anos.

## Seus dados

Como nada é armazenado de forma persistente, não há histórico para consultar ou
apagar: sair da sala e fechar o app já elimina tudo que existia. Para remover
uma mensagem publicada no Discord, apague-a pelo próprio Discord.

## Contato

[Abra uma questão no GitHub](https://github.com/dragonrange/Vysor/issues).
