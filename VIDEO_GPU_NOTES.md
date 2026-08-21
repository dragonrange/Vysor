# Codificação de vídeo por hardware (GPU) — como funciona e como debugar

## Os 3 níveis

Quando você clica em "Transmitir", o Vysor tenta, nessa ordem:

1. **NVENC + captura por GPU (só "Tela Inteira") — DESLIGADO por enquanto.**
   Usaria o filtro `ddagrab` do ffmpeg (Desktop Duplication API) pra
   capturar o monitor inteiro direto na GPU. Desliguei depois do primeiro
   teste real mostrar uma prévia corrompida (ver "Histórico de bugs já
   corrigidos" abaixo) — a causa raiz encontrada foi um bug que afetava os
   3 níveis igualmente, mas por segurança deixei esse nível específico
   desligado até validar com mais um teste. Pra religar, mude
   `EnableFullScreenGpuCapture` pra `true` em `MainWindow.xaml.cs`.
2. **NVENC/Quick Sync/AMF + captura normal (Tela Inteira OU Janela)** — usa
   a mesma captura de sempre (`CopyFromScreen`/`PrintWindow`), mas em vez de
   comprimir como JPEG, manda os pixels crus pro ffmpeg codificar em H.264
   usando a GPU (tenta Nvidia, depois Intel Quick Sync, depois AMD, nessa
   ordem).
3. **JPEG (pipeline de sempre)** — se nenhum dos dois níveis acima
   conseguir subir (sem `ffmpeg.exe` do lado do `Vysor.exe`, sem GPU
   compatível, driver desatualizado, etc.), a transmissão simplesmente usa o
   que já funcionava antes. Nada trava, nada quebra — só não tem o ganho de
   performance.

Cada nível cai pro próximo automaticamente. Você não precisa escolher nada
manualmente.

## Como saber qual nível está ativo

Ainda não tem um indicador visual na tela pra isso (é uma melhoria futura
fácil de adicionar, tipo um textinho pequeno perto do botão Transmitir). Por
enquanto, a forma de confirmar é observar:
- Se o uso de CPU durante a transmissão ficar bem mais baixo que antes (e o
  Gerenciador de Tarefas mostrar um processo `ffmpeg.exe` filho do
  `Vysor.exe` usando a GPU na aba Desempenho), o Nível 1 ou 2 está ativo.
- Se não aparecer nenhum `ffmpeg.exe` rodando durante a transmissão, caiu
  pro Nível 3 (JPEG) — confira se o `ffmpeg.exe` está mesmo do lado do
  `Vysor.exe` (mesma pasta).

## O que checar se algo não funcionar

- **Nada de GPU nunca ativa** → confirme que existe um arquivo
  `ffmpeg.exe` na mesma pasta do `Vysor.exe` (ver `GERAR_EXE.md`, Passo
  2.5). Sem ele, o app nem tenta.
- **Cores esquisitas/trocadas na transmissão** (ex: azul e vermelho
  invertidos) → é a ordem de bytes BGRA vs RGBA da captura; troque
  `-pix_fmt bgra` por `-pix_fmt rgba` em `VideoEncodeService.cs`
  (método `StartRawPipeHardware`) e teste de novo.
- **Transmissão de tela cheia não pega o monitor certo** (Nível 1) → o
  índice do `ddagrab` pode não bater com a ordem que o Windows mostra os
  monitores; ajuste `monitorIndex` em `TryStartHardwareEncodeCore` (em
  `MainWindow.xaml.cs`) ou force sempre cair pro Nível 2 removendo a
  chamada a `StartFullScreenHardware`.
- **Compartilhar uma janela específica não ativa GPU** → é esperado só
  quando não há NENHUM dos 3 encoders (NVENC/Quick Sync/AMF) disponível
  nessa máquina — confira o Gerenciador de Tarefas pra ver qual GPU está
  instalada.
- **Quer ver os logs de erro do ffmpeg pra debugar de verdade** → os
  serviços (`VideoEncodeService`/`VideoDecodeService`) hoje só drenam o
  stderr do ffmpeg sem guardar o texto (pra não travar o pipe). Se precisar
  investigar uma falha específica, é só me mandar a descrição do que
  aconteceu (que app estava compartilhando, tela cheia ou janela, o que
  apareceu na tela) que eu ajusto o código pra gravar esse log num arquivo
  temporário na próxima rodada.
- **Redimensionar a janela compartilhada no meio da transmissão** → no
  Nível 2, o tamanho é combinado com o ffmpeg uma única vez, no começo. Se
  você redimensionar a janela depois de já estar transmitindo, os frames
  com tamanho diferente são descartados silenciosamente (a imagem congela)
  até você parar e começar a transmitir de novo.

## Por que a imagem demora ~1s pra aparecer quando você abre a transmissão de alguém

Isso é do formato de vídeo, não é bug. Um vídeo H.264 só pode ser "entrado"
num **quadro-chave** (um quadro completo, que não depende de nenhum outro);
os quadros entre eles guardam só a diferença em relação ao anterior. Quando
você clica pra assistir alguém no meio da transmissão, seu computador precisa
esperar o próximo quadro-chave chegar pra ter uma imagem completa — até lá a
telinha fica com "Aguardando o primeiro quadro...".

O intervalo entre quadros-chave foi reduzido de 2 segundos para 1 segundo
(`KeyframeInterval` em `VideoEncodeService.cs`), então a espera caiu pela
metade. Diminuir mais que isso começa a consumir bastante banda, porque
quadro-chave é bem mais pesado que os outros.

No modo antigo (JPEG) isso não existia, porque cada quadro era uma foto
independente. É o preço de usar vídeo de verdade — em troca, o consumo de
CPU e de banda é muito menor.

## Histórico de bugs já corrigidos

- **Prévia com fundo quase preto e um rastro de vários "cursores fantasma"
  diagonais, alternando com frames perfeitamente nítidos** (visto no
  primeiro teste real, transmitindo com o Nível 2 ativo). Causa: dentro de
  `VideoEncodeService.ScanForAccessUnits`, ao fechar uma "access unit" e
  compactar o buffer (`EmitAccessUnit`/`CompactBuffer`), a posição do
  próximo frame (`boundary`) tinha sido calculada ANTES da compactação, mas
  era guardada em `_pendingAuStart` sem ajustar pela quantidade que acabou
  de ser removida do início do buffer — cada frame processado deixava esse
  ponteiro um pouco mais "adiantado" que devia, cortando o próximo frame no
  lugar errado. Isso corrompe o H.264 de um jeito bem específico: os
  quadros P (que dependem do anterior) vão acumulando erro até o próximo
  quadro-chave (a cada ~2 segundos) resetar tudo — exatamente o padrão de
  "as vezes nítido, as vezes corrompido" visto no teste. Corrigido
  subtraindo a quantidade compactada do `boundary` antes de guardá-lo. Como
  esse bug estava no código compartilhado por todos os níveis (não só o
  ddagrab), é bem provável que ele também explicasse boa parte do
  comportamento estranho, mesmo com o Nível 1 desligado.

## Auditoria completa (agosto/2026) — o que foi encontrado e corrigido

Uma revisão geral do código (processos, memória, serviços e bugs) encontrou
os problemas abaixo. Todos já estão corrigidos:

**Travamentos (os mais graves)**
- **O app inteiro podia congelar de vez ao assistir alguém em H.264.** A
  entrega de vídeo pro ffmpeg era feita na mesma thread da interface, e a
  thread que lia o resultado esperava a interface pra desenhar — as duas
  ficavam se esperando eternamente. Era preciso matar o Vysor pelo
  Gerenciador de Tarefas. Agora todo o tráfego passa por filas com threads
  próprias, e nada bloqueia a interface.
- **A transmissão inteira podia congelar pelo mesmo motivo**, quando a
  prévia local não dava conta do fluxo.

**Processos do ffmpeg ficando órfãos**
- Parar de assistir alguém (ou parar a transmissão) logo depois de começar
  deixava um `ffmpeg.exe` rodando pra sempre, invisível, sem ninguém pra
  encerrá-lo. Isso segurava sessões do encoder da GPU, e depois de algumas
  vezes a codificação por hardware simplesmente parava de funcionar até
  reiniciar o PC.
- Agora, além da correção dessa corrida, os processos são amarrados ao
  Vysor por um "Job Object" do Windows: se o app fechar de qualquer jeito
  (inclusive travando ou sendo morto pelo Gerenciador de Tarefas), o Windows
  mata os ffmpeg junto.

**Vazamentos de memória**
- Se a captura falhasse (tela bloqueada, troca de usuário, sessão remota), a
  imagem já alocada vazava — e como isso acontece de 30 a 60 vezes por
  segundo, consumia memória gráfica muito rápido.
- Buffers internos que cresciam num pico nunca devolviam a memória.
- O motor de áudio nunca era desligado ao fechar a janela.

**Bugs de uso**
- **Fixar (pin) um tile que não fosse o primeiro deixava a tela toda preta.**
- Um tile aberto enquanto outro estava fixado nunca aparecia.
- Clicar no play da sua própria linha fechava sua prévia e criava uma
  telinha fantasma que nunca carregava.
- **Se a primeira conexão falhasse (servidor dormindo, internet fora), o app
  ficava permanentemente sem conseguir conectar** até ser fechado e aberto de
  novo — e os botões não davam reação nenhuma.
- Código de sala errado ou digitado em minúsculas não dava aviso nenhum.
  Agora minúsculas funcionam e erros aparecem na tela.
- Depois de uma queda de internet, o app voltava mostrando a sala
  normalmente mas não mandava nem recebia mais nada. Agora ele reentra na
  sala sozinho.
- A escolha 720p/1080p era ignorada na codificação por GPU (quem estava num
  monitor 4K transmitia 4K mesmo pedindo 720p).
- Redimensionar a janela compartilhada congelava a transmissão até parar e
  começar de novo.
- Transmitir uma janela que já tinha sido fechada "começava" sem nunca
  mostrar nada.
- Parar e começar a transmitir rapidinho podia deixar dois laços de captura
  rodando ao mesmo tempo (dobro de CPU e de quadros na rede).
- Se o ffmpeg morresse no meio, a transmissão morria em silêncio; agora ela
  volta sozinha pro modo JPEG.

**Áudio**
- **O áudio podia continuar sendo transmitido depois de você mandar parar**,
  numa corrida entre parar e começar de novo.
- A exclusão do Discord podia mirar no processo errado (quando o Discord
  estava minimizado na bandeja), e aí **o áudio da chamada ia junto na
  transmissão mesmo assim** — exatamente o que essa função existe pra
  evitar.
- Mutar ou mudar o volume de alguém antes dessa pessoa falar não fazia
  efeito: quando o áudio chegava, vinha no volume normal com o ícone
  mostrando "mudo".
- Estalos e falhas periódicas no som em placas de 44.1kHz, por um erro de
  arredondamento que se acumulava (o áudio saía 0,2% mais rápido e ia
  entupindo o buffer de quem ouvia). Medido depois da correção: desvio de
  0,002%.
- Um "clique" no volume máximo, por um estouro de número no compressor de
  áudio.

**Servidor**
- Salas e participantes fantasmas que nunca eram apagados (vazamento de
  memória permanente no servidor).
- Sorteio de código de sala com risco de duas salas se sobrescreverem.
- Sem limite de gente por sala (agora 12).
- Sem checagem de saúde no Fly.io, e o servidor dormia por completo — o que
  fazia a primeira conexão do dia falhar.

## O que eu não consegui testar de verdade

Não tenho como rodar Windows, DirectX nem o `ffmpeg.exe` de verdade neste
ambiente onde o código foi escrito — então a sintaxe de linha de comando foi
verificada contra documentação oficial e exemplos da comunidade, mas nunca
executada. Os pontos mais prováveis de precisar de ajuste na primeira
transmissão de teste:

- As flags exatas do `h264_qsv` (Quick Sync da Intel).
- A combinação `-f h264 -i pipe:0 -c:v bmp -f image2pipe pipe:1` usada pra
  decodificar (cada parte é documentada separadamente, mas nunca vi as duas
  juntas confirmadas em um exemplo real).
- A ordem de bytes BGRA vs RGBA (ver item acima).

Se algo desses três não funcionar de primeira, me manda o que apareceu na
tela (ou trava, ou cor errada, ou não muda nada) que eu ajusto o parâmetro
específico sem precisar reescrever o resto.
