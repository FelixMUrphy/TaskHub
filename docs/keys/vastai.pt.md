# Vast.ai: chave de API para o TaskHub

[English](vastai.md) · [Українська](vastai.uk.md) · [Español](vastai.es.md) · [Deutsch](vastai.de.md) · [Français](vastai.fr.md) · [Italiano](vastai.it.md) · **Português** · [Polski](vastai.pl.md) · [中文](vastai.zh.md) · [日本語](vastai.ja.md) · [Todas as páginas](README.md)

## Para que o TaskHub usa a chave
- **Acompanhar:** suas instâncias, a carga delas, o preço, as cobranças dos últimos 30 dias, seu saldo e seu crédito.
- **Ações** (só se a chave permitir): Parar, Iniciar, Reinicializar — pelo painel, por uma notificação ou pelo Telegram.
- **Excluir (destroy)** nunca é feito automaticamente. Só funciona no painel do TaskHub, depois de você digitar o nome.

## Permissões mínimas
| O que você quer | Permissões |
|---|---|
| (a) Só acompanhar | `instance_read`, `user_read`, `billing_read` |
| (b) Parar / Iniciar / Reinicializar | as acima + `instance_write` |

Ressalva: o `instance_write` do Vast.ai cobre "creating, managing, and destroying instances and volumes" — não
existe permissão para parar / iniciar sem poder excluir. O TaskHub nunca exclui por conta própria, somente pelo painel, depois de você
digitar o nome.

## Crie a chave
**No console:** abra a seção **Keys** do console do Vast.ai → **API Keys** → **+New**. Na janela que aparece,
dê um nome (por exemplo, `TaskHub`) e escolha as permissões — todas as permissões da sua conta vêm marcadas por
padrão, então deixe só as da tabela. Crie a chave e copie-a.

**Pela CLI:** as páginas de permissões do Vast.ai descrevem chaves restritas pela CLI / API — use esse caminho se o
console não permitir restringir as permissões. Salve como `perms.json`:

```json
{
    "api": {
        "instance_read": {},
        "user_read": {},
        "billing_read": {}
    }
}
```

Adicione `"instance_write": {}` para as ações e execute:

```
vastai create api-key --name "TaskHub" --permission_file perms.json
```

## Cole no TaskHub
1. TaskHub → **Configurações** → **Conectores** → **Vast.ai**.
2. Ative o conector, cole a chave e clique em **Salvar**.
3. A linha deve mostrar **Conectado, saldo …** (ou **Conectado**). Se o Vast.ai rejeitar a chave, nada é salvo.

O TaskHub verifica uma vez (e depois diariamente) se a chave pode executar ações e só mostra os botões de ação se ela puder.

## Onde a chave fica guardada
- Somente no Gerenciador de Credenciais do Windows — nunca em `config.json`.
- Enviada somente para `console.vast.ai`.

## Revogue a chave
- **No Vast.ai:** console → **Keys** → **Delete** ao lado da chave (**Reset** a substitui por uma nova).
- **No TaskHub:** Configurações → Conectores → Vast.ai → **Remover** (apaga a chave do Gerenciador de Credenciais).

## Documentação oficial
- Chaves de API: <https://docs.vast.ai/guides/reference/keys>
- Permissões: <https://docs.vast.ai/api-reference/permissions>, <https://docs.vast.ai/cli/permissions>
- Cobrança e saldo: <https://docs.vast.ai/guides/reference/billing>
