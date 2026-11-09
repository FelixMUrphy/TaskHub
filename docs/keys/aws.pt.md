# AWS: chave de acesso para o TaskHub

[English](aws.md) · [Українська](aws.uk.md) · [Español](aws.es.md) · [Deutsch](aws.de.md) · [Français](aws.fr.md) · [Italiano](aws.it.md) · **Português** · [Polski](aws.pl.md) · [中文](aws.zh.md) · [日本語](aws.ja.md) · [Todas as páginas](README.md)

## Para que o TaskHub usa a chave
- **Acompanhar:** suas instâncias EC2 nas Regiões que você usa: nome, tipo, Região, estado e há quanto tempo estão em execução.
  Uma instância em execução por mais tempo que o limite definido por você (72 horas por padrão) recebe um lembrete "Esquecida?".
- **Tarefas longas, se você ativar:** jobs de treinamento do SageMaker e jobs do AWS Batch: estado, tempo, motivo da falha. Cada um tem a sua própria
  chave de ativação, desativada por padrão. O SageMaker não informa uma porcentagem para jobs de treinamento comuns, então o TaskHub não mostra uma.
- **Custos, se você ativar:** o custo e a previsão do mês atual do Cost Explorer (a AWS cobra por essas requisições, veja abaixo) e seus AWS Budgets. Um
  orçamento acima do limite gera uma notificação por mês.
- **Nunca:** o TaskHub não inicia, não para nem altera nada na sua conta. Esta política não lhe dá meios para isso.

O TaskHub lê cada Região a cada 10 minutos. A página de preços do EC2 da AWS não lista cobrança por essas chamadas, mas a AWS não diz explicitamente que são gratuitas.

> Este conector foi criado a partir da documentação da AWS e testado em um simulador, ainda não em uma conta real da AWS.

A AWS prefere credenciais temporárias. O TaskHub precisa de uma chave de longo prazo de um usuário IAM dedicado, com esta política somente de leitura, e você
pode revogá-la a qualquer momento. Nunca use as chaves do usuário root.

## Permissões mínimas
Crie uma política com este JSON. Versões futuras do conector podem precisar de mais permissões, e esta página vai listá-las.

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "TaskHubList",
      "Effect": "Allow",
      "Action": ["ec2:DescribeInstances", "ec2:DescribeRegions"],
      "Resource": "*"
    }
  ]
}
```

Para acompanhar tarefas longas, adicione as instruções (statements) das que você ativar:

```json
{"Sid": "TaskHubSageMakerList", "Effect": "Allow", "Action": "sagemaker:ListTrainingJobs", "Resource": "*"},
{"Sid": "TaskHubSageMakerJob", "Effect": "Allow", "Action": "sagemaker:DescribeTrainingJob", "Resource": "arn:aws:sagemaker:*:*:training-job/*"},
{"Sid": "TaskHubBatch", "Effect": "Allow", "Action": ["batch:DescribeJobQueues", "batch:ListJobs", "batch:DescribeJobs"], "Resource": "*"}
```

`sagemaker:DescribeTrainingJob` e `batch:DescribeJobs` retornam a descrição completa de um job, que pode incluir suas configurações e variáveis
de ambiente. O TaskHub lê apenas o estado, os horários, o limite de tempo e o motivo da falha.

Custos, se você ativar:

```json
{"Sid": "TaskHubCost", "Effect": "Allow", "Action": ["ce:GetCostAndUsage", "ce:GetCostForecast"], "Resource": "*"},
{"Sid": "TaskHubBudgets", "Effect": "Allow", "Action": ["budgets:ViewBudget", "budgets:DescribeBudgets"], "Resource": "*"}
```

**O Cost Explorer é pago.** A AWS cobra US$ 0,01 por requisição. O TaskHub faz no máximo 2 a cada 8 horas, cerca de US$ 1,80 por mês, e guarda a
resposta para que uma reinicialização não gere nova cobrança. A AWS não lista cobrança pela leitura de orçamentos. O Cost Explorer precisa ser ativado uma vez no
[console de Billing](https://docs.aws.amazon.com/cost-management/latest/userguide/ce-enable.html); não é possível ativá-lo pela API, e os dados aparecem em cerca de
um dia. Uma conta nova pode ficar sem previsão até completar um ciclo de cobrança. Em uma AWS Organization, a conta de gerenciamento pode bloquear o acesso das contas-membro ao
Cost Explorer, qualquer que seja a política delas.

Para ler os orçamentos, o TaskHub também consulta `sts:GetCallerIdentity` uma vez para obter o número da conta. A AWS não exige permissão para essa chamada. O número fica só na memória.

`ec2:DescribeRegions` descobre as Regiões ativadas na sua conta. Sem ela, o TaskHub acompanha apenas as Regiões que você listar
manualmente, ou `us-east-1` se você não listar nenhuma.

## Crie a chave
1. No [console do IAM](https://console.aws.amazon.com/iam/), crie um usuário para o TaskHub (não precisa de acesso ao console) e anexe
   a política acima.
2. Abra **Users**, selecione o usuário e abra a guia **Security credentials**.
3. Em **Access keys**, escolha **Create access key**.
4. Escolha **Other** como caso de uso, depois **Next** e **Create access key**.
5. Escolha **Show** e copie o **Access key ID** e a **Secret access key**. A AWS mostra o segredo só uma vez.

Chaves que começam com `ASIA` são temporárias e o TaskHub as recusa. Use uma chave que comece com `AKIA`.

## Cole no TaskHub
1. TaskHub → **Configurações** → **Conectores** → **AWS**.
2. Ative o conector, cole o ID da chave de acesso e o segredo nos campos correspondentes e clique em **Salvar**.
3. A linha deve mostrar **Conectado**. Se a AWS rejeitar a chave, nada é salvo.

Deixe **Regiões** vazio para acompanhar todas as Regiões ativadas na conta, ou adicione apenas as que você usa.

| A linha diz | O que significa |
|---|---|
| A chave funciona, mas a política dela não permite `ec2:DescribeRegions` | Adicione a permissão acima ou indique suas Regiões |
| Sem acesso nesta Região | A política ou a conta não permite esta Região. As outras Regiões continuam funcionando |
| SageMaker ou AWS Batch: a chave não tem acesso | Adicione as instruções acima ou desative essa opção |
| Cost Explorer: a chave não tem acesso ou ele não está ativado | Adicione a instrução acima e ative o Cost Explorer no console de Billing |
| Cost Explorer: ainda sem dados | Ativado há pouco: aguarde cerca de um dia |
| Verifique o relógio deste computador | A AWS recusa requisições assinadas com a hora errada. Corrija o relógio; a chave está correta |

## Onde a chave fica guardada
- Somente no Gerenciador de Credenciais do Windows, nunca em `config.json`. As duas partes ficam guardadas juntas e nunca são mostradas de novo.
- O segredo em si nunca é enviado: ele assina cada requisição. As requisições vão somente para `ec2.<region>.amazonaws.com` e, para o que você ativar, `api.sagemaker.<region>.amazonaws.com` e `batch.<region>.amazonaws.com`, `ce.us-east-1.amazonaws.com`, `budgets.amazonaws.com` e `sts.amazonaws.com`.

## Revogue a chave
- **Na AWS:** console do IAM → **Users** → o usuário → **Security credentials** → **Access keys** → **Actions** → **Deactivate** e depois
  **Delete**.
- **No TaskHub:** Configurações → Conectores → AWS → **Remover** (apaga a chave do Gerenciador de Credenciais).

## Documentação oficial
- Criar, desativar e excluir chaves de acesso: <https://docs.aws.amazon.com/IAM/latest/UserGuide/access-keys-admin-managed.html>,
  <https://docs.aws.amazon.com/IAM/latest/UserGuide/access-key-self-managed.html>
- Chaves de acesso e credenciais temporárias: <https://docs.aws.amazon.com/IAM/latest/UserGuide/id_credentials_access-keys.html>,
  <https://docs.aws.amazon.com/IAM/latest/UserGuide/security-creds-programmatic-access.html>
- Boas práticas: <https://docs.aws.amazon.com/IAM/latest/UserGuide/best-practices.html>
- As chamadas: <https://docs.aws.amazon.com/AWSEC2/latest/APIReference/API_DescribeInstances.html>,
  <https://docs.aws.amazon.com/AWSEC2/latest/APIReference/API_DescribeRegions.html>
- Tarefas longas: <https://docs.aws.amazon.com/sagemaker/latest/APIReference/API_ListTrainingJobs.html>,
  <https://docs.aws.amazon.com/sagemaker/latest/APIReference/API_DescribeTrainingJob.html>,
  <https://docs.aws.amazon.com/batch/latest/APIReference/API_ListJobs.html>,
  <https://docs.aws.amazon.com/batch/latest/APIReference/API_DescribeJobs.html>,
  <https://docs.aws.amazon.com/batch/latest/APIReference/API_DescribeJobQueues.html>
- Custos: <https://docs.aws.amazon.com/aws-cost-management/latest/APIReference/API_GetCostAndUsage.html>,
  <https://docs.aws.amazon.com/aws-cost-management/latest/APIReference/API_GetCostForecast.html>,
  <https://aws.amazon.com/aws-cost-management/aws-cost-explorer/pricing/>,
  <https://docs.aws.amazon.com/aws-cost-management/latest/APIReference/API_budgets_DescribeBudgets.html>,
  <https://docs.aws.amazon.com/STS/latest/APIReference/API_GetCallerIdentity.html>
- Regiões: <https://docs.aws.amazon.com/accounts/latest/reference/manage-acct-regions.html>
