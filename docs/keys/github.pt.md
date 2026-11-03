# GitHub Actions: token para o TaskHub

[English](github.md) · [Українська](github.uk.md) · [Español](github.es.md) · [Deutsch](github.de.md) · [Français](github.fr.md) · [Italiano](github.it.md) · **Português** · [Polski](github.pl.md) · [中文](github.zh.md) · [日本語](github.ja.md) · [Todas as páginas](README.md)

## Para que o TaskHub usa o token
- **Acompanhar:** as execuções de workflow dos repositórios que você listar — progresso, a etapa que falhou, o resultado.
- **Ações** (só com acesso de escrita): Cancelar e Reexecutar uma execução.
- **Pull requests** (opcional, a partir da 2.9.4, somente leitura): os que aguardam a sua revisão e os pull requests abertos do Dependabot.
- O TaskHub nunca altera seus workflows, seu código nem as configurações dos repositórios.

## Crie um token fine-grained
1. GitHub → **Settings** → **Developer settings** → **Personal access tokens** → **Fine-grained tokens** →
   **Generate new token** (link direto: <https://github.com/settings/personal-access-tokens/new>).
2. **Name:** por exemplo, `TaskHub`.
3. **Expiration:** 30 dias por padrão; escolha o que for melhor para você (o TaskHub avisa uma semana antes de ele expirar).
4. **Resource owner:** você ou a organização dona dos repositórios.
5. **Repository access:** **Only select repositories** → os repositórios que você vai listar no TaskHub.
6. **Permissions** (veja abaixo) → **Generate token** → copie o token.

Se o dono for uma organização que exige aprovação, o token fica pendente até um administrador aprová-lo.

## Permissões mínimas
| O que você quer | Permissões do repositório |
|---|---|
| (a) Só acompanhar | **Actions:** Read-only · **Contents:** Read-only · **Metadata:** Read-only |
| (b) Cancelar / Reexecutar | **Actions:** Read and write · **Contents:** Read-only · **Metadata:** Read-only |

Pull requests (opcional): os pull requests do Dependabot precisam também de **Pull requests: Read-only** (o GitHub só lista os
pull requests abertos com essa permissão); a busca de pedidos de revisão não precisa de permissão. Um token fine-grained vê os pedidos
de revisão apenas nos repositórios que lhe foram concedidos e nos públicos.

Prefira este token à outra opção do TaskHub (o token que o Git Credential Manager já tem), que tem
acesso a todos os seus repositórios.

## Acompanhar mais um repositório depois
Com **Only select repositories**, o token alcança só os repositórios que você escolheu. Se o TaskHub disser que um repositório
*não foi encontrado ou o token não tem acesso a ele*: confira o nome e depois abra o token no GitHub (**Settings** → **Developer settings** →
**Personal access tokens** → **Fine-grained tokens**) e adicione o repositório em **Repository access**. O GitHub responde da mesma forma para
um nome errado, um repositório que o token não recebeu e, em algumas requisições, uma permissão ausente; por isso o TaskHub não consegue dizer qual é o caso.
[GitHub: 404 for an existing resource](https://docs.github.com/en/rest/using-the-rest-api/troubleshooting-the-rest-api)

## Cole no TaskHub
1. TaskHub → **Configurações** → **Conectores** → **GitHub Actions**.
2. Ative o conector, escolha **Meu próprio token fine-grained (recomendado)**, cole o token, liste os repositórios
   (`owner/repo`) e clique em **Salvar**.
3. A linha deve mostrar **Conectado como** *seu login*. Se o GitHub rejeitar o token, nada é salvo.

## Onde o token fica guardado
- Somente no Gerenciador de Credenciais do Windows — nunca em `config.json`.
- Enviado somente para `api.github.com`. O trecho final do log (opcional) é buscado pelo link de uso único que o GitHub fornece,
  sem o token.

## Revogue o token
- **No GitHub:** Settings → Developer settings → Personal access tokens → Fine-grained tokens → o token →
  **Delete**.
- **No TaskHub:** Configurações → Conectores → GitHub Actions → **Remover** (apaga o token do Gerenciador de Credenciais).

## Documentação oficial
- Criar um token fine-grained:
  <https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens#creating-a-fine-grained-personal-access-token>
- Buscar pull requests (`review-requested:`):
  <https://docs.github.com/en/search-github/searching-on-github/searching-issues-and-pull-requests>
- Permissões por endpoint:
  <https://docs.github.com/en/rest/authentication/permissions-required-for-fine-grained-personal-access-tokens>
