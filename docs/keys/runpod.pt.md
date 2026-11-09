# Runpod: chave de API para o TaskHub

[English](runpod.md) · [Українська](runpod.uk.md) · [Español](runpod.es.md) · [Deutsch](runpod.de.md) · [Français](runpod.fr.md) · [Italiano](runpod.it.md) · **Português** · [Polski](runpod.pl.md) · [中文](runpod.zh.md) · [日本語](runpod.ja.md) · [Todas as páginas](README.md)

## Para que o TaskHub usa a chave
- **Acompanhar:** seus pods, a carga deles, o preço, o gasto até agora (cobrança do Runpod), seu saldo e a autonomia restante.
- **Ações** (só se a chave permitir): Parar, Iniciar, Reiniciar — pelo painel, por uma notificação ou pelo Telegram.
- **Excluir** nunca é feito automaticamente. Só funciona no painel do TaskHub, depois de você digitar o nome do pod.

## Crie a chave
1. Abra o console do Runpod: <https://console.runpod.io/user/credentials>.
2. Em **API Keys**, clique em **Create API Key**.
3. Digite um nome (por exemplo, `TaskHub`) e escolha o tipo (veja abaixo).
4. Clique em **Create** e copie a chave imediatamente — ela aparece só uma vez; o Runpod não a guarda.

## Qual tipo escolher
| O que você quer | Tipo | Observações |
|---|---|---|
| (a) Só acompanhar | **Read Only** | Recomendado se você não precisa dos botões. O Runpod não documenta se uma chave Read Only pode ler a cobrança e o saldo; se o TaskHub não mostrar o saldo, é por isso. |
| (b) Parar / Iniciar / Reiniciar | **All** | O Runpod documenta escopos **Restricted** apenas para endpoints Serverless, não para pods — por isso, para ações em pods, use **All**. |

O TaskHub verifica uma vez (e depois diariamente) se a chave pode executar ações e só mostra os botões de ação se ela puder.
Uma chave do tipo **All** também poderia excluir pods — o TaskHub nunca faz isso por conta própria, somente pelo painel, depois de
você digitar o nome.

## Cole no TaskHub
1. TaskHub → **Configurações** → **Conectores** → **Runpod**.
2. Ative o conector, cole a chave e clique em **Salvar**.
3. A linha deve mostrar **Conectado, saldo …** (ou **Conectado**). Se o Runpod rejeitar a chave, nada é salvo.

## Onde a chave fica guardada
- Somente no Gerenciador de Credenciais do Windows — nunca em `config.json`.
- Enviada somente para `api.runpod.io` (REST para pods, tamanhos de volumes de rede e cobrança; GraphQL para o saldo).

## Revogue a chave
- **No Runpod:** <https://console.runpod.io/user/credentials> → o ícone de lixeira ao lado da chave → **Revoke Key**
  (ou desative a chave no botão de alternância).
- **No TaskHub:** Configurações → Conectores → Runpod → **Remover** (apaga a chave do Gerenciador de Credenciais).
  Desinstalar o TaskHub com **Excluir também meus dados** também a apaga; uma desinstalação simples a mantém.

## Documentação oficial
- Chaves de API: <https://docs.runpod.io/get-started/credentials>
- Limites de requisições: <https://docs.runpod.io/api-reference-v2/rate-limits>
- Preços e o que acontece em $0: <https://docs.runpod.io/pods/pricing>
