# pt-BR

## Description

O TaskHub reúne em um só lugar as tarefas demoradas do seu PC e permite controlá-las: exportação de vídeo, renderizações, builds, backups, treinamento de modelos e os scripts que agentes de IA para programação iniciam para você.

Como funciona
Um programa ou script informa o próprio progresso ao TaskHub por uma API HTTP local em 127.0.0.1, pelo comando taskhub ou pelo cliente Python incluído. O TaskHub mostra:
• um ícone na bandeja com um anel de progresso e o número de tarefas ativas;
• um painel com o progresso, o tempo restante, o status e o log de cada tarefa, além dos widgets que ela pedir: gráficos de linha, de área e de barras, medidores, números, indicadores, tabelas;
• os controles da própria tarefa: botões, campos de número, texto ou lista, pausar, parar e encerrar. O que você escolhe volta para a tarefa;
• notificações do Windows quando uma tarefa termina, falha ou para de dar notícias, com os botões da tarefa se ela pedir.

"taskhub run -- <comando>" envolve qualquer linha de comando: a saída vai para o log da tarefa, o progresso é lido dela e são adicionados os botões Parar e Encerrar.

Claude Code e Codex
No menu da bandeja (Agentes de IA) você conecta o TaskHub ao Claude Code e ao Codex. O agente recebe as ferramentas do TaskHub (MCP) para informar suas tarefas demoradas, e você recebe uma notificação quando o agente termina uma resposta ou precisa da sua permissão. O TaskHub adiciona as próprias entradas aos arquivos de configuração dos agentes e as remove quando você desativa a opção.

Telegram (opcional)
Conecte seu próprio bot do Telegram para receber as mesmas notificações no celular e tocar nos botões das tarefas por lá. As mensagens vão do seu PC direto para a Bot API do Telegram.

Privacidade
Sem conta, sem serviço na nuvem, sem telemetria. A API escuta somente em 127.0.0.1, e suas tarefas e configurações ficam em %APPDATA%\TaskHub.

Bom saber
• As atualizações chegam pela Microsoft Store.
• A inicialização automática pode ser desativada no menu da bandeja ou em Configurações → Aplicativos → Inicialização.
• A referência da API abre pelo menu da bandeja (Referência da API).
• Requer Windows 10 versão 1809 ou posterior (x64) e o Microsoft Edge WebView2 Runtime, que o Windows atual já inclui.
• Idiomas da interface: inglês, ucraniano, espanhol, alemão, francês, italiano, português, polonês, chinês (simplificado), japonês.

## Product features

- Ícone na bandeja com anel de progresso e número de tarefas ativas
- Painel com progresso, tempo restante, log, gráficos, medidores e tabelas para cada tarefa
- Botões e campos declarados pela própria tarefa: pausar, parar, alterar um parâmetro, encerrar
- Notificações do Windows ao concluir, falhar ou travar, com os botões da tarefa
- API HTTP local, comando taskhub e cliente Python; "taskhub run" envolve qualquer comando
- Claude Code e Codex: ferramentas do TaskHub (MCP) e notificações, ativadas pelo menu da bandeja
- Bot do Telegram opcional: notificações, progresso e botões da tarefa no celular
- Sem conta, sem nuvem, sem telemetria; a API escuta somente em 127.0.0.1
- Interface em 10 idiomas

## Short description

Acompanhe e controle as tarefas demoradas do seu PC: progresso, gráficos, notificações e os botões de cada tarefa. Funciona com qualquer script por uma API local ou pelo comando taskhub, e também com Claude Code e Codex.
