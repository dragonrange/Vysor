; Instalador do Vysor (Windows).
;
; POR QUE ISTO EXISTE
; O vídeo/áudio agora vão SEMPRE direto entre os PCs (P2P por UDP) — o
; servidor não tem mais nenhum jeito de repassar mídia (ver RoomHub.cs).
; Pra isso funcionar, o Windows Firewall precisa deixar ENTRAR tráfego UDP
; não solicitado no Vysor.exe — e por padrão ele bloqueia isso, silenciosamente
; ou atrás de um popup que é fácil de fechar sem perceber. Foi exatamente essa
; falta de regra que fez um teste real não mostrar nenhum vídeo.
;
; Este instalador resolve isso configurando a regra de firewall UMA VEZ, na
; instalação (que já pede elevação de administrador de qualquer forma) — cada
; amigo que instalar o Vysor por aqui nunca mais precisa mexer em Firewall
; na mão.
;
; COMO GERAR O INSTALADOR
; 1. Publique o app (ver GERAR_EXE.md): dotnet publish ... -o publish
; 2. Coloque o ffmpeg.exe (opcional, aceleração por GPU) e o server.txt
;    (opcional, endereço fixo do servidor) do lado do Vysor.exe dentro de
;    "publish", se você usa esses dois.
; 3. Compile este arquivo com o Inno Setup (ISCC.exe Vysor.iss, ou abra no
;    editor do Inno Setup e aperte Compilar). O instalador final sai em
;    installer\Output\VysorSetup.exe.

#define MyAppName "Vysor"
#define MyAppExeName "Vysor.exe"
; Precisa bater EXATAMENTE com VysorLink.Scheme no código (Services/VysorLink.cs).
#define MyAppUrlScheme "vysor"
#define MyAppPublisher "Vysor"
#define SourceDir "..\publish"

; Lida DIRETO do Vysor.exe já compilado (o número vem do <Version> do
; VysorClient.csproj, gravado no arquivo pelo dotnet publish) — nunca mais
; digitado à mão aqui. Já aconteceu duas vezes de esquecer de manter isto
; sincronizado com o csproj, o que fazia o app achar pra sempre que tinha
; uma atualização (baixava, instalava, e a versão instalada continuava
; "antiga" porque o número no instalador nunca tinha sido o certo). Lendo
; do próprio .exe, essa classe de erro deixa de poder acontecer: o que o
; instalador diz que é SEMPRE bate com o que foi publicado de verdade.
#define MyAppVersion GetVersionNumbersString(SourceDir + "\" + MyAppExeName)

; ASSINATURA DIGITAL (pendente — ver abaixo)
;
; Sem assinatura, o Smart App Control do Windows 11 recusa o instalador com o
; erro 4551 ("uma política de Controle de Aplicativo bloqueou este arquivo").
; Não é falha do Vysor: ele bloqueia qualquer executável sem assinatura de uma
; autoridade confiável.
;
; Certificado AUTOASSINADO não resolve — o Windows exige uma cadeia até uma
; autoridade em que ele já confia. Ou seja, isto depende de um certificado
; adquirido em nome do responsável pelo app; não há solução por código.
;
; QUANDO TIVER O CERTIFICADO, basta descomentar as duas linhas abaixo e
; registrar a ferramenta no Inno Setup (Tools > Configure Sign Tools),
; apontando "assinador" para algo como:
;   signtool.exe sign /f "C:\caminho\certificado.pfx" /p SENHA /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $f
;
; O carimbo de tempo (/tr) não é opcional na prática: sem ele, a assinatura
; deixa de valer quando o certificado expira, e instaladores já publicados
; voltariam a ser recusados.
;
; SignTool=assinador
; SignedUninstaller=yes

[Setup]
AppId={{6B6E4C8B-2B7B-4D6C-9A0E-6C9D3E7F8A2B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=Output
OutputBaseFilename=VysorSetup
SetupIconFile=..\vysor.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; Precisa ser admin PORQUE a regra de firewall precisa — é o único motivo.
; O app em si nunca roda elevado.
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
; Padrão do Inno Setup já é "yes", mas deixamos explícito de propósito: é o
; que permite instalar por cima com o Vysor.exe rodando (o botão "Atualizar"
; dentro do próprio app conta com isso — ver UpdateChecker.cs/BtnUpdate_Click
; no MainWindow.xaml.cs). Sem isto, atualizar exigiria fechar o app na mão
; primeiro.
CloseApplications=yes

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar um atalho na área de trabalho"; GroupDescription: "Atalhos adicionais:"

[Files]
Source: "{#SourceDir}\Vysor.exe"; DestDir: "{app}"; Flags: ignoreversion
; Os dois abaixo são opcionais: se você não usa GPU/servidor fixo, o
; instalador simplesmente pula (skipifsourcedoesntexist) sem dar erro.
Source: "{#SourceDir}\ffmpeg.exe"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#SourceDir}\server.txt"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Registry]
; Registra o "vysor://" como link clicável do Windows.
;
; É isto que faz um convite colado no Discord virar um clique em vez de um
; código pra digitar — e é a mesma peça que o botão "Entrar" do Discord usa
; por baixo, porque tudo termina em "abra o Vysor e entre na sala X".
;
; O "URL Protocol" vazio não é decoração: é a marca que o Windows procura pra
; decidir que esta chave descreve um protocolo, e sem ela o link não funciona
; mesmo com todo o resto certo.
;
; uninsdeletekey na chave raiz limpa tudo isto ao desinstalar — senão ficaria
; um protocolo registrado apontando pra um programa que não existe mais, e
; clicar num convite antigo daria erro do Windows sem explicação.
Root: HKLM; Subkey: "Software\Classes\{#MyAppUrlScheme}"; ValueType: string; ValueName: ""; ValueData: "URL:Vysor"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\{#MyAppUrlScheme}"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKLM; Subkey: "Software\Classes\{#MyAppUrlScheme}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"",0"
Root: HKLM; Subkey: "Software\Classes\{#MyAppUrlScheme}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Desinstalar {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; A peça que resolve o furo de NAT de vez: libera ENTRADA de UDP pro Vysor no
; Firewall do Windows. "profile=any" cobre rede doméstica, pública ou de
; trabalho — sem isso, quem estivesse num perfil de rede sem regra
; equivalente ficaria bloqueado do mesmo jeito.
Filename: "netsh.exe"; Parameters: "advfirewall firewall add rule name=""Vysor"" dir=in action=allow program=""{app}\{#MyAppExeName}"" enable=yes profile=any protocol=UDP"; Flags: runhidden; StatusMsg: "Liberando o Vysor no Firewall do Windows (necessário pra transmissão direta funcionar)..."
Filename: "{app}\{#MyAppExeName}"; Description: "Abrir o Vysor agora"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Desinstalar tira a regra também — não deixa lixo acumulando no Firewall a
; cada instalação/desinstalação.
Filename: "netsh.exe"; Parameters: "advfirewall firewall delete rule name=""Vysor"" program=""{app}\{#MyAppExeName}"""; Flags: runhidden; RunOnceId: "RemoveVysorFirewallRule"
