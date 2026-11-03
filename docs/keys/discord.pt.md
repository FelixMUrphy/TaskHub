# Discord: token de bot para o TaskHub

[English](discord.md) · [Українська](discord.uk.md) · [Español](discord.es.md) · [Deutsch](discord.de.md) · [Français](discord.fr.md) · [Italiano](discord.it.md) · **Português** · [Polski](discord.pl.md) · [中文](discord.zh.md) · [日本語](discord.ja.md) · [Todas as páginas](README.md)

## Para que o TaskHub usa o token
- Envia suas notificações do TaskHub, o progresso das tarefas e gráficos como mensagens diretas do **seu próprio** bot.
- Recebe os toques nos botões e os comandos `/tasks`, `/mute`, `/unmute`, por uma conexão que o seu PC abre com o Discord. Não é preciso um endereço público.
- Não lê suas mensagens: não pede intents privilegiadas e nunca pede o conteúdo das mensagens.

## Crie o bot
1. Abra o Discord Developer Portal: <https://discord.com/developers/applications?new_application=true>.
2. Clique em **Create App**, digite um nome (por exemplo, `TaskHub`) e clique em **Create**.
3. Na página **Bot**, em **Token**, clique em **Reset Token** e copie o token. O Discord o mostra só uma vez.
4. Mantenha o bot só seu: na página **Installation**, defina **Install Link** como **None** e, na página **Bot**, desative **Public Bot**. Com ele desativado, só você pode adicionar o bot a servidores. (Se o Portal recusar, faça primeiro a etapa da página **Installation**.)
5. Deixe o resto como está: nenhuma intent privilegiada e **Interactions Endpoint URL** vazio. Com uma URL ali, o Discord envia os toques nos botões para ela em vez de enviar ao TaskHub, e o TaskHub avisa você.

## Cole no TaskHub
1. TaskHub → **Configurações** → **Discord**. Cole o token e clique em **Conectar**. Um token que o Discord recusa não é salvo.
2. Adicione o bot a um servidor (próxima seção). O Discord só deixa um bot escrever para quem compartilha um servidor com ele; até lá, o TaskHub informa que o bot ainda não pode escrever para você.
3. Clique em **Verificar novamente**. O bot envia uma mensagem direta com um botão **Conectar**. Toque nele.

Só o dono do aplicativo pode ser vinculado (no caso de um aplicativo de equipe, o dono da equipe). Só os toques dessa pessoa, nesse chat, têm efeito.

## Adicione o bot a um servidor
1. No TaskHub, clique em **Adicionar o bot a um servidor**. A página do Discord abre no seu navegador.
2. Na lista de servidores, escolha um em que você possa adicionar bots: você é o dono ou tem **Manage Server** nele. O bot não recebe nenhuma permissão nele e não o lê; só precisa compartilhar um servidor com você.
3. Confirme nessa página, volte ao TaskHub e clique em **Verificar novamente**.

**Não tem um servidor seu?** Criar um é grátis: no app do Discord, clique em **Add a Server** na parte de baixo da barra lateral esquerda, escolha **Create My Own** e depois **For me and my friends**, digite um nome e clique em **Create**. Depois repita as etapas acima.

**Ainda sem mensagem?** Suas configurações de privacidade podem estar bloqueando: clique no nome do servidor, abra **Privacy Settings**, ative **Allow direct messages from server members** e clique em **Verificar novamente**.

## Onde o token fica guardado
- Somente no Gerenciador de Credenciais do Windows, nunca em `config.json`. Se o Gerenciador de Credenciais não estiver disponível, o TaskHub avisa e não conecta.
- Enviado somente para `discord.com` e para o gateway do Discord (`*.discord.gg`).
- Seu ID de usuário do Discord, o ID do chat e o ID do aplicativo ficam guardados em `config.json`.

## Revogue o token
- **No Discord:** Developer Portal → seu aplicativo → **Bot** → **Reset Token**. O token antigo para de funcionar.
- **No TaskHub:** Configurações → Discord → **Desconectar** (apaga o token do Gerenciador de Credenciais). O bot continua na sua conta do Discord até você excluir o aplicativo. Desinstalar o TaskHub com **Excluir também meus dados** também apaga o token; uma desinstalação simples o mantém.

## Se o token deixar de funcionar
O Discord redefine o token de um bot após 1000 novas conexões em um dia e avisa o dono por e-mail. O TaskHub fica bem abaixo desse limite e espera quando chega perto. Se acontecer mesmo assim, a página informa que o token foi rejeitado: crie um novo e conecte de novo.

## Documentação oficial
- Primeiros passos: <https://docs.discord.com/developers/quick-start/getting-started>
- Bots e mensagens diretas, servidores em comum: <https://docs.discord.com/developers/interactions/application-commands>
- Criar um servidor: <https://support.discord.com/hc/en-us/articles/204849977-How-do-I-create-a-server>
- Mensagens diretas de membros do servidor: <https://support.discord.com/hc/en-us/articles/217916488-Blocking-Privacy-Settings>
- Quem pode adicionar um bot (Public Bot): <https://docs.discord.com/developers/topics/oauth2>
- Interactions Endpoint URL e o gateway: <https://docs.discord.com/developers/interactions/receiving-and-responding>
- Limites de conexão: <https://docs.discord.com/developers/events/gateway>
- Política de privacidade: <https://discord.com/privacy>
