# GitHub Actions : jeton pour TaskHub

[English](github.md) · [Українська](github.uk.md) · [Español](github.es.md) · [Deutsch](github.de.md) · **Français** · [Italiano](github.it.md) · [Português](github.pt.md) · [Polski](github.pl.md) · [中文](github.zh.md) · [日本語](github.ja.md) · [Toutes les pages](README.md)

## À quoi TaskHub l'utilise
- **Surveiller :** les exécutions de workflow des dépôts que vous indiquez — progression, étape en échec, résultat.
- **Actions** (uniquement avec un accès en écriture) : annuler et relancer une exécution.
- **Pull requests** (facultatif, en lecture seule) : celles qui attendent votre relecture et les pull requests Dependabot ouvertes.
- TaskHub ne modifie jamais vos workflows, votre code ni les paramètres de vos dépôts.

## Créer un jeton fine-grained
1. GitHub → **Settings** → **Developer settings** → **Personal access tokens** → **Fine-grained tokens** →
   **Generate new token** (lien direct : <https://github.com/settings/personal-access-tokens/new>).
2. **Name :** par exemple `TaskHub`.
3. **Expiration :** 30 jours par défaut ; choisissez ce qui vous convient (TaskHub vous prévient une semaine avant l'expiration).
4. **Resource owner :** vous, ou l'organisation propriétaire des dépôts.
5. **Repository access :** **Only select repositories** → les dépôts que vous indiquerez dans TaskHub.
6. **Permissions** (voir ci-dessous) → **Generate token** → copiez-le.

Si le propriétaire est une organisation qui exige une approbation, le jeton reste en attente jusqu'à ce qu'un administrateur l'approuve.

## Autorisations minimales
| Vous voulez | Repository permissions |
|---|---|
| (a) Surveiller uniquement | **Actions:** Read-only · **Contents:** Read-only · **Metadata:** Read-only |
| (b) Annuler / relancer | **Actions:** Read and write · **Contents:** Read-only · **Metadata:** Read-only |

Pull requests (facultatif) : les pull requests Dependabot demandent aussi **Pull requests: Read-only** (GitHub ne liste les
pull requests ouvertes qu'avec cette autorisation) ; la recherche des demandes de relecture n'en demande aucune. Un jeton
fine-grained ne voit les demandes de relecture que dans les dépôts auxquels il a accès et dans les dépôts publics.

Préférez ce jeton à l'autre option de TaskHub (le jeton que le Git Credential Manager possède déjà), qui a
accès à tous vos dépôts.

## Surveiller un dépôt de plus plus tard
Avec **Only select repositories**, le jeton n'atteint que les dépôts que vous avez choisis. Si TaskHub indique qu'un dépôt est
*introuvable, ou que le jeton ne peut pas l'atteindre* : vérifiez le nom, puis ouvrez le jeton sur GitHub (**Settings** → **Developer settings** →
**Personal access tokens** → **Fine-grained tokens**) et ajoutez le dépôt sous **Repository access**. GitHub répond de la même façon pour
un nom erroné, un dépôt auquel le jeton n'a pas été donné et, pour certaines requêtes, une autorisation manquante : TaskHub ne peut donc pas vous dire laquelle de ces causes est en jeu.
[GitHub : 404 pour une ressource existante](https://docs.github.com/en/rest/using-the-rest-api/troubleshooting-the-rest-api)

## Le coller dans TaskHub
1. TaskHub → **Paramètres** → **Connecteurs** → **GitHub Actions**.
2. Activez-le, choisissez **Mon propre jeton fine-grained (recommandé)**, collez le jeton, indiquez les dépôts
   (`owner/repo`), cliquez sur **Enregistrer**.
3. La ligne doit indiquer **Connecté en tant que** *votre identifiant*. Si GitHub refuse le jeton, rien n'est enregistré.

## Où le jeton est conservé
- Uniquement dans le Gestionnaire d'identification Windows — jamais dans `config.json`.
- Envoyé uniquement à `api.github.com`. La fin du journal, facultative, est récupérée via le lien à usage unique que GitHub fournit,
  sans le jeton.

## Le révoquer
- **Sur GitHub :** Settings → Developer settings → Personal access tokens → Fine-grained tokens → le jeton →
  **Delete**.
- **Dans TaskHub :** Paramètres → Connecteurs → GitHub Actions → **Supprimer** (le retire du Gestionnaire d'identification).

## Documentation officielle
- Créer un jeton fine-grained :
  <https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens#creating-a-fine-grained-personal-access-token>
- Rechercher des pull requests (`review-requested:`) :
  <https://docs.github.com/en/search-github/searching-on-github/searching-issues-and-pull-requests>
- Autorisations par endpoint :
  <https://docs.github.com/en/rest/authentication/permissions-required-for-fine-grained-personal-access-tokens>
