# SSH: configurando um host para o TaskHub

[English](ssh.md) · [Українська](ssh.uk.md) · [Español](ssh.es.md) · [Deutsch](ssh.de.md) · [Français](ssh.fr.md) · [Italiano](ssh.it.md) · **Português** · [Polski](ssh.pl.md) · [中文](ssh.zh.md) · [日本語](ssh.ja.md) · [Todas as páginas](README.md)

## O que o TaskHub faz com ele
- O TaskHub executa o `ssh.exe` do próprio Windows e lê cada host que você listar: carga, memória, discos, GPU, sessões do tmux e um processo que você indicar.
- **O TaskHub não guarda chave nem senha.** Ele usa o seu `~/.ssh/config`, o seu agente SSH e o seu `known_hosts`, e apenas os lê.
- Ele não instala nada no host e não altera nada lá. Por enquanto, funciona com hosts Linux.

## Antes de adicionar um host
O TaskHub não consegue responder a perguntas, então três coisas precisam funcionar sem nenhuma.

1. **O cliente OpenSSH.** O TaskHub usa o do Windows, não uma cópia do Git. Em um terminal, `C:\Windows\System32\OpenSSH\ssh.exe -V` deve mostrar uma versão. Se não mostrar: Configurações do Windows → Sistema → Recursos opcionais (no Windows 10: Aplicativos → Recursos opcionais) → adicione *Cliente OpenSSH* ([passos da Microsoft](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_install_firstuse)).
2. **Uma chave sem precisar digitar nada.** Uma chave com senha (passphrase) precisa estar no agente. No PowerShell como administrador, uma vez:
   ```powershell
   Get-Service ssh-agent | Set-Service -StartupType Automatic
   Start-Service ssh-agent
   ```
   Depois, com o seu próprio usuário: `ssh-add`. ([Guia da Microsoft sobre chaves](https://learn.microsoft.com/en-us/windows-server/administration/openssh/openssh_keymanagement).) Agentes do 1Password ou do Pageant funcionam se atenderem o OpenSSH do Windows.
3. **A chave do host é confiável.** Conecte uma vez em um terminal: `ssh my-server`. Compare a impressão digital (fingerprint) mostrada com a que o dono do host lhe passou e responda `yes`. O TaskHub nunca confia em uma chave nova por conta própria.

A verificação: `C:\Windows\System32\OpenSSH\ssh.exe -o BatchMode=yes my-server uname -s` mostra `Linux` sem fazer perguntas.

O nome de um host vai no `~/.ssh/config`, junto com o usuário, a porta e a chave:
```
Host my-server
    HostName 203.0.113.10
    User me
    IdentityFile ~/.ssh/id_ed25519
```

## Adicione-o
Configurações → *Conectores* → *SSH* → ative o conector, digite `my-server` e clique em *Adicionar*. Apenas letras, números, pontos, hifens e sublinhados: `user@host` vai no config.

## O que você vê
Uma tarefa para cada host, com a carga e os discos. Você recebe uma notificação quando um disco enche, quando a conexão é perdida ou o TaskHub precisa parar de tentar, quando o host reinicia e quando um job termina:
- **uma sessão do tmux**, ou um comando dentro dela voltando ao shell (fechar um editor também conta; uma opção em Configurações desativa isso, e os avisos continuam no log da tarefa);
- **um processo que você indicar:** *Observar um processo* na tarefa do host, com o número dele (`pgrep -f train`, por exemplo).

O TaskHub não consegue saber como um job terminou, então diz "terminou", não "concluído com sucesso". Enquanto o seu PC está desligado, ninguém está observando. Um processo indicado por você que terminou nesse intervalo é informado na próxima conexão. Mudanças no tmux e a reinicialização do host só são percebidas durante uma conexão perdida, enquanto o TaskHub continua em execução: não depois que o TaskHub ou o seu PC reiniciam.

## O que é bom saber
- **Seu config é executado.** Um `ProxyCommand` ou `Match exec` no `~/.ssh/config` é executado em cada conexão que o TaskHub faz, como seria para você em um terminal. Hosts atrás de `ProxyJump` usam as configurações do seu config para o host intermediário.
- **O que fica aberto.** Um `ssh` por host enquanto o conector está ativado. Ele envia um script fixo, somente de leitura, a cada minuto por padrão (no mínimo a cada 30 segundos).
- **Uma chave alterada interrompe tudo.** Se a chave de um host mudar, o TaskHub não conecta e avisa você. Pode ser um host reinstalado ou alguém no meio do caminho: verifique e depois corrija o `known_hosts` você mesmo.
- **Logins que falham.** Depois de uma chave recusada, o TaskHub para de tentar até você clicar em *Verificar novamente*, para não fazer o seu endereço ser banido.
- **O que sai do seu PC.** Apenas para os seus hosts: seu nome de usuário e chaves públicas (como qualquer ssh faz) e o script. O que volta fica na pasta de dados do TaskHub e aparece nas notificações, inclusive no Telegram ou no Discord, quando ativados. Veja [PRIVACY.md](../../PRIVACY.md).

## Parar
Desative o conector ou remova o host: a conexão termina. Nada fica no host, e não há chave no TaskHub para apagar.
