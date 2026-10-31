# pt-BR

## Description

O TaskHub reúne em um só lugar as tarefas demoradas do seu PC e permite controlá-las: exportação de vídeo, renderizações, builds, backups, treinamento de modelos, execuções de CI, máquinas GPU na nuvem e os scripts que agentes de IA para programação iniciam para você.

Como funciona
Um programa ou script informa o próprio progresso ao TaskHub por uma API HTTP local em 127.0.0.1, pelo comando taskhub ou pelo cliente Python incluído. O TaskHub mostra:
• um ícone na bandeja com um anel de progresso e o número de tarefas ativas;
• um painel com o progresso, o tempo restante, o status e o log de cada tarefa, além dos widgets que ela pedir: gráficos de linha, de área e de barras, medidores, números, indicadores, tabelas;
• os controles da própria tarefa: botões, campos de número, texto ou lista, pausar, parar e encerrar. O que você escolhe volta para a tarefa;
• notificações do Windows quando uma tarefa termina, falha ou para de dar notícias, com os botões da tarefa se ela pedir.

"taskhub run -- <comando>" envolve qualquer linha de comando: a saída vai para o log da tarefa, o progresso é lido dela e são adicionados os botões Parar e Encerrar.

Configurações no painel
Tudo se configura em uma única página: o botão ⚙ do painel (ou Configurações no menu da bandeja) abre aparência, notificações e Telegram, conectores, agentes de IA, inicialização automática e a referência da API. O menu da bandeja mantém as ações rápidas: abrir o painel, silenciar, configurações, apoiar, sair.

Claude Code e Codex
Em Configurações → Agentes de IA você conecta o TaskHub ao Claude Code e ao Codex. O agente recebe as ferramentas do TaskHub (MCP) para informar suas tarefas demoradas, e você recebe uma notificação quando o agente termina uma resposta ou precisa da sua permissão. O TaskHub adiciona as próprias entradas aos arquivos de configuração dos agentes e as remove quando você desativa a opção.

Conectores (opcionais, desativados por padrão)
• GitHub Actions: as execuções de workflows dos repositórios que você indicar, como tarefas: progresso por jobs e etapas, a etapa que falhou e o fim do log dela, botões para cancelar e executar de novo.
• Runpod e Vast.ai: suas máquinas GPU como tarefas: GPU, preço por hora, gasto até agora, tempo ligada, SSH, gráficos de carga, aviso de "GPU ociosa", saldo e autonomia com aviso de saldo baixo, parar / iniciar / reiniciar (excluir só no painel, depois de digitar o nome da máquina).
Cada conector só funciona depois que você o ativa e informa sua própria chave de API. A chave fica guardada apenas no Gerenciador de Credenciais do Windows, e o conector fala somente com a API do próprio serviço (api.github.com, api.runpod.io, console.vast.ai).

Proteção de saldo (opcional, desativada até você ativá-la)
Para Runpod e Vast.ai: um aviso no seu próprio limite de saldo e, no limite de parada, as máquinas ligadas são paradas depois de uma notificação com Parar agora e Adiar. Ela só para as máquinas e nunca as exclui; se não conseguir ler o saldo, não para nada.

Telegram (opcional)
Conecte seu próprio bot do Telegram para receber no celular as mesmas notificações e uma mensagem de progresso ao vivo por tarefa, e tocar nos botões das tarefas por lá. O token do bot fica no Gerenciador de Credenciais do Windows, e as mensagens vão do seu PC direto para a Bot API do Telegram.

Privacidade
Sem conta, sem telemetria. A API escuta somente em 127.0.0.1, e suas tarefas e configurações ficam em %APPDATA%\TaskHub. Nada sai do seu PC a menos que você ative o Telegram ou um conector.

Bom saber
• As atualizações chegam pela Microsoft Store.
• A inicialização automática pode ser desativada em Configurações → Sistema ou nas Configurações do Windows → Aplicativos → Inicialização.
• A referência da API abre pela página de configurações.
• Requer Windows 10 versão 2004 ou posterior (x64) e o Microsoft Edge WebView2 Runtime, que o Windows atual já inclui.
• Idiomas da interface: inglês, ucraniano, espanhol, alemão, francês, italiano, português, polonês, chinês (simplificado), japonês.

## What's new in this version

TaskHub 2.9
• Página de configurações no painel (⚙): tudo em um só lugar; o menu da bandeja mantém as ações rápidas, no estilo do Windows 11.
• Novos conectores, desativados por padrão: GitHub Actions (execuções de workflows com cancelar e executar de novo), Runpod e Vast.ai (máquinas GPU, gasto, saldo, aviso de GPU ociosa, parar e iniciar). Suas chaves de API ficam apenas no Gerenciador de Credenciais do Windows.
• Proteção de saldo para Runpod e Vast.ai, desativada até você ativá-la: um aviso no seu limite e, no limite de parada, as máquinas ligadas são paradas, nunca excluídas.
• Telegram: o token do bot passa para o Gerenciador de Credenciais do Windows; o TaskHub respeita o ritmo de mensagens do Telegram em vez de perder mensagens.
• Ajuste de transparência dos cartões; links para arquivos e pastas são mostrados no Explorador, nunca executados.
• 2.9.1: valores em dinheiro com duas casas decimais, custo de armazenamento dos network volumes do Runpod.
• Agora requer Windows 10 versão 2004 ou posterior.

## Product features

- Ícone na bandeja com anel de progresso e número de tarefas ativas
- Painel com progresso, tempo restante, log, gráficos, medidores e tabelas para cada tarefa
- Botões e campos declarados pela própria tarefa: pausar, parar, alterar um parâmetro, encerrar
- Notificações do Windows ao concluir, falhar ou travar, com os botões da tarefa
- API HTTP local, comando taskhub e cliente Python; "taskhub run" envolve qualquer comando
- Todas as configurações em uma página do painel (⚙); ações rápidas no menu da bandeja
- Claude Code e Codex: ferramentas do TaskHub (MCP) e notificações, ativadas nas configurações
- Conector opcional do GitHub Actions: execuções de workflows como tarefas, com cancelar e executar de novo
- Conectores opcionais do Runpod e Vast.ai: máquinas GPU, gasto, saldo, aviso de GPU ociosa, parar e iniciar
- Proteção de saldo opcional: avisa com saldo baixo e para (nunca exclui) as máquinas ligadas
- Bot do Telegram opcional: notificações, progresso e botões da tarefa no celular
- Sem conta, sem telemetria; chaves de API só no Gerenciador de Credenciais do Windows; a API escuta somente em 127.0.0.1
- Interface em 10 idiomas

## Short description

Acompanhe e controle as tarefas demoradas do seu PC: progresso, gráficos, notificações e os botões de cada tarefa. Funciona com qualquer script por uma API local ou pelo comando taskhub, com Claude Code e Codex e, se quiser, com GitHub Actions, Runpod e Vast.ai.
