# Deploy do VysorServer no Render

## 1. Suba o projeto para o GitHub

O repositório pode continuar contendo `VysorClient` e `VysorServer`. O `render.yaml` na raiz aponta o Render para `VysorServer/Dockerfile` e usa `VysorServer` como contexto Docker.

## 2. No Render

1. Abra o Dashboard do Render.
2. **New → Blueprint**.
3. Conecte o repositório do Vysor.
4. Selecione a branch principal.
5. Confira o serviço `vysorserver`.
6. Escolha o plano **Free** para o primeiro teste.
7. Faça o deploy.

O serviço precisa ser um **Web Service**, porque o SignalR precisa de conexões HTTP/WebSocket públicas.

## 3. Teste

Quando o Render fornecer a URL, abra:

```text
https://SEU-SERVICO.onrender.com/
```

Deve aparecer:

```text
Vysor — servidor de sinalização rodando! ✅
```

Depois teste:

```text
https://SEU-SERVICO.onrender.com/status
```

## 4. Atualizar o cliente

O cliente tem um endereço padrão em `VysorClient/Services/SignalRService.cs`, mas não é necessário recompilar se você distribuir um arquivo `server.txt` ao lado do `Vysor.exe`.

O conteúdo deve ser uma única linha terminando em `/roomhub`, por exemplo:

```text
https://SEU-SERVICO.onrender.com/roomhub
```

Sem aspas e sem espaços extras.

## 5. Domínio próprio

Depois que tudo funcionar, você pode apontar, por exemplo, `server.seudominio.com` para o Web Service do Render. O Render fornece TLS/HTTPS gerenciado para o domínio.

## Observação sobre o Free

O Free Service entra em sleep após 15 minutos sem tráfego de entrada e leva cerca de um minuto para acordar. Uma nova conexão WebSocket também pode acordá-lo. Conexões ativas podem ser interrompidas quando a instância é reiniciada/substituída, então o cliente deve continuar usando reconexão automática.

Para o Vysor, isso é aceitável para testes e uso hobby. Vídeo e áudio nunca passam por aqui — o servidor não tem mais nenhum método capaz de repassar mídia (foi isso que estourou o plano grátis duas vezes); só sinalização (códigos de sala, endereços, listas de participantes) trafega pelo WebSocket, sempre uns poucos KB.
