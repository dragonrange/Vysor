# VysorServer

Servidor de sinalização do Vysor (ASP.NET Core + SignalR) — só sala, códigos e
troca de endereços pra conexão direta. Não repassa vídeo/áudio de jeito
nenhum (ver RoomHub.cs): foi repassar mídia que estourou o plano grátis do
Render duas vezes.

## Render

Este servidor está preparado para ser publicado como **Web Service Docker** no Render.
O Render fornece a variável `PORT`; o servidor escuta em `0.0.0.0:$PORT`, com `10000` como fallback local. Render suporta conexões WebSocket em Web Services, que é o transporte usado pelo SignalR quando disponível.

O arquivo `render.yaml`, na raiz do repositório, cria/configura o serviço automaticamente usando o Dockerfile de `VysorServer`.

### Configuração manual

- Type: **Web Service**
- Runtime: **Docker**
- Dockerfile: `VysorServer/Dockerfile`
- Docker context: `VysorServer`
- Health check: `/status`
- Instance: `Free` para testes/hobby

A URL pública atual é:

```text
https://vysorserver-cjxi.onrender.com
```

O endpoint do SignalR é:

```text
https://vysorserver-cjxi.onrender.com/roomhub
```

> O nome exato do subdomínio pode mudar se o serviço for recriado no Render
> (o `vysorserver` puro já não estava disponível, por isso o sufixo). Se
> mudar de novo, coloque a URL real em `server.txt` ao lado do `Vysor.exe`
> (Windows) ou em `VYSOR_SERVER` (Linux); os clientes já suportam essa
> substituição sem recompilar. O Android tem a URL fixa em
> `SignalRClient.HUB` — esse precisa de recompilação.

## Rodando localmente

```bash
dotnet run
```

Localmente, o servidor usa `PORT` se ela existir; caso contrário, usa a porta `10000`.

## Limitação importante

As salas ficam em memória. Um restart/redeploy apaga o estado atual das salas. O cliente possui reconexão/reentrada para reduzir o impacto.

## Sobre o relay de vídeo/áudio (removido de propósito)

Este servidor **não tem nenhum método capaz de repassar vídeo ou áudio**. Já
teve — um caminho de reserva pra quando o furo de NAT falhava pra um par
específico — e foi exatamente esse caminho (mais o relay amplo que existia
antes dele) que estourou o plano grátis do Render duas vezes. Todo vídeo/áudio
vai OBRIGATORIAMENTE direto entre os clientes (UDP + furo de NAT); se o
caminho direto não fechar, o quadro é descartado no cliente, não repassado
por aqui. Não reintroduza `SendScreenFrame`/`SendAudioChunk`/variantes sem
entender essa decisão — ver o comentário no topo de `RoomHub.cs`.
