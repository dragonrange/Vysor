# VysorServer

Servidor de sinalização e retransmissão de fallback do Vysor (ASP.NET Core + SignalR).

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

A URL pública será parecida com:

```text
https://vysorserver.onrender.com
```

O endpoint do SignalR é:

```text
https://vysorserver.onrender.com/roomhub
```

> O nome exato do subdomínio pode mudar se `vysorserver` não estiver disponível. Nesse caso, coloque a URL real em `server.txt` ao lado do `Vysor.exe`; o cliente já suporta essa substituição sem recompilar.

## Rodando localmente

```bash
dotnet run
```

Localmente, o servidor usa `PORT` se ela existir; caso contrário, usa a porta `10000`.

## Limitação importante

As salas ficam em memória. Um restart/redeploy apaga o estado atual das salas. O cliente possui reconexão/reentrada para reduzir o impacto.

O servidor ainda contém métodos de relay de vídeo/áudio como fallback. O caminho preferencial continua sendo P2P UDP entre os clientes.
