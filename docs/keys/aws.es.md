# AWS: clave de acceso para TaskHub

[English](aws.md) · [Українська](aws.uk.md) · **Español** · [Deutsch](aws.de.md) · [Français](aws.fr.md) · [Italiano](aws.it.md) · [Português](aws.pt.md) · [Polski](aws.pl.md) · [中文](aws.zh.md) · [日本語](aws.ja.md) · [Todas las páginas](README.md)

## Para qué la usa TaskHub
- **Vigilar:** tus instancias EC2 en las regiones que uses: nombre, tipo, región, estado y cuánto tiempo llevan en marcha.
  Una instancia que lleve en marcha más del límite que fijes (72 horas por defecto) recibe un aviso de "¿Olvidada?".
- **Trabajos largos, si los activas:** trabajos de entrenamiento de SageMaker y trabajos de AWS Batch: estado, tiempo, motivo del fallo. Cada uno tiene su propio
  interruptor, desactivado por defecto. SageMaker no informa de un porcentaje en los trabajos de entrenamiento normales, así que TaskHub no lo muestra.
- **Costes, si los activas:** el coste del mes y la previsión de Cost Explorer (AWS cobra estas solicitudes, ver abajo) y tus AWS Budgets. Un
  presupuesto que supere su límite genera una notificación al mes.
- **Nunca:** TaskHub no inicia, detiene ni cambia nada en tu cuenta. Esta política no le da forma de hacerlo.

TaskHub lee cada región cada 10 minutos. La página de precios de EC2 de AWS no menciona ningún cargo por estas llamadas, pero AWS no dice expresamente que sean gratuitas.

> Este conector se ha creado a partir de la documentación de AWS y se ha probado con un simulador, todavía no con una cuenta real de AWS.

AWS prefiere las credenciales temporales. TaskHub necesita una clave de larga duración para un usuario de IAM dedicado con esta política de solo lectura, y puedes
revocarla en cualquier momento. No uses nunca las claves del usuario root.

## Permisos mínimos
Crea una política con este JSON. Las versiones posteriores del conector pueden necesitar más permisos, y esta página los indicará.

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

Para seguir trabajos largos, añade las instrucciones de los que actives:

```json
{"Sid": "TaskHubSageMakerList", "Effect": "Allow", "Action": "sagemaker:ListTrainingJobs", "Resource": "*"},
{"Sid": "TaskHubSageMakerJob", "Effect": "Allow", "Action": "sagemaker:DescribeTrainingJob", "Resource": "arn:aws:sagemaker:*:*:training-job/*"},
{"Sid": "TaskHubBatch", "Effect": "Allow", "Action": ["batch:DescribeJobQueues", "batch:ListJobs", "batch:DescribeJobs"], "Resource": "*"}
```

`sagemaker:DescribeTrainingJob` y `batch:DescribeJobs` devuelven la descripción completa de un trabajo, que puede incluir su configuración y sus variables
de entorno. TaskHub lee solo el estado, los tiempos, el límite de tiempo y el motivo del fallo.

Costes, si los activas:

```json
{"Sid": "TaskHubCost", "Effect": "Allow", "Action": ["ce:GetCostAndUsage", "ce:GetCostForecast"], "Resource": "*"},
{"Sid": "TaskHubBudgets", "Effect": "Allow", "Action": ["budgets:ViewBudget", "budgets:DescribeBudgets"], "Resource": "*"}
```

**Cost Explorer es de pago.** AWS cobra 0,01 $ por solicitud. TaskHub hace 2 como máximo cada 8 horas, unos 1,80 $ al mes, y recuerda la
respuesta para que un reinicio no vuelva a pagar. AWS no indica ningún cargo por leer los presupuestos. Cost Explorer debe activarse una vez en la
[consola de Billing](https://docs.aws.amazon.com/cost-management/latest/userguide/ce-enable.html); no se puede activar mediante la API, y los datos aparecen en aproximadamente
un día. Una cuenta nueva puede no tener previsión hasta que complete un ciclo de facturación. En una AWS Organization, la cuenta de administración puede bloquear el acceso de las cuentas miembro a
Cost Explorer, diga lo que diga su política.

Para leer los presupuestos, TaskHub también pide una vez el número de cuenta con `sts:GetCallerIdentity`. AWS no exige ningún permiso para esa llamada. El número se queda en memoria.

`ec2:DescribeRegions` encuentra las regiones habilitadas en tu cuenta. Sin él, TaskHub vigila solo las regiones que indiques
tú mismo, o `us-east-1` si no indicas ninguna.

## Crea la clave
1. En la [consola de IAM](https://console.aws.amazon.com/iam/), crea un usuario para TaskHub (no necesita acceso a la consola) y adjúntale
   la política anterior.
2. Abre **Users**, selecciona el usuario y abre la pestaña **Security credentials**.
3. En **Access keys**, elige **Create access key**.
4. Elige **Other** como caso de uso, luego **Next** y después **Create access key**.
5. Elige **Show** y copia tanto el **Access key ID** como el **Secret access key**. AWS muestra el secreto una sola vez.

Las claves que empiezan por `ASIA` son temporales y TaskHub las rechaza. Usa una clave que empiece por `AKIA`.

## Pégala en TaskHub
1. TaskHub → **Configuración** → **Conectores** → **AWS**.
2. Actívalo, pega el ID de la clave de acceso y el secreto en sus campos, y haz clic en **Guardar**.
3. La línea debe decir **Conectado**. Si AWS rechaza la clave, no se guarda nada.

Deja **Regiones** vacío para vigilar todas las regiones habilitadas en la cuenta, o añade solo las que uses.

| La línea dice | Qué significa |
|---|---|
| La clave funciona, pero su política no permite `ec2:DescribeRegions` | Añade el permiso anterior o indica tus regiones |
| Sin acceso en esta región | La política o la cuenta no permite esta región. Las demás regiones siguen funcionando |
| SageMaker o AWS Batch: la clave no tiene acceso | Añade las instrucciones anteriores o desactiva ese interruptor |
| Cost Explorer: la clave no tiene acceso o no está activado | Añade la instrucción anterior y activa Cost Explorer en la consola de Billing |
| Cost Explorer: aún no hay datos | Activado hace poco: espera aproximadamente un día |
| Revisa el reloj de este equipo | AWS rechaza las solicitudes firmadas con una hora incorrecta. Corrige el reloj; la clave está bien |

## Dónde se guarda la clave
- Solo en el Administrador de credenciales de Windows, nunca en `config.json`. Las dos partes se guardan juntas y no se vuelven a mostrar.
- El secreto en sí nunca se envía: sirve para firmar cada solicitud. Las solicitudes van únicamente a `ec2.<region>.amazonaws.com` y, para lo que actives, a `api.sagemaker.<region>.amazonaws.com` y `batch.<region>.amazonaws.com`, `ce.us-east-1.amazonaws.com`, `budgets.amazonaws.com` y `sts.amazonaws.com`.

## Revócala
- **En AWS:** consola de IAM → **Users** → el usuario → **Security credentials** → **Access keys** → **Actions** → **Deactivate** y luego
  **Delete**.
- **En TaskHub:** Configuración → Conectores → AWS → **Quitar** (la elimina del Administrador de credenciales).

## Documentación oficial
- Crear, desactivar y eliminar claves de acceso: <https://docs.aws.amazon.com/IAM/latest/UserGuide/access-keys-admin-managed.html>,
  <https://docs.aws.amazon.com/IAM/latest/UserGuide/access-key-self-managed.html>
- Claves de acceso y credenciales temporales: <https://docs.aws.amazon.com/IAM/latest/UserGuide/id_credentials_access-keys.html>,
  <https://docs.aws.amazon.com/IAM/latest/UserGuide/security-creds-programmatic-access.html>
- Buenas prácticas: <https://docs.aws.amazon.com/IAM/latest/UserGuide/best-practices.html>
- Las llamadas: <https://docs.aws.amazon.com/AWSEC2/latest/APIReference/API_DescribeInstances.html>,
  <https://docs.aws.amazon.com/AWSEC2/latest/APIReference/API_DescribeRegions.html>
- Trabajos largos: <https://docs.aws.amazon.com/sagemaker/latest/APIReference/API_ListTrainingJobs.html>,
  <https://docs.aws.amazon.com/sagemaker/latest/APIReference/API_DescribeTrainingJob.html>,
  <https://docs.aws.amazon.com/batch/latest/APIReference/API_ListJobs.html>,
  <https://docs.aws.amazon.com/batch/latest/APIReference/API_DescribeJobs.html>,
  <https://docs.aws.amazon.com/batch/latest/APIReference/API_DescribeJobQueues.html>
- Costes: <https://docs.aws.amazon.com/aws-cost-management/latest/APIReference/API_GetCostAndUsage.html>,
  <https://docs.aws.amazon.com/aws-cost-management/latest/APIReference/API_GetCostForecast.html>,
  <https://aws.amazon.com/aws-cost-management/aws-cost-explorer/pricing/>,
  <https://docs.aws.amazon.com/aws-cost-management/latest/APIReference/API_budgets_DescribeBudgets.html>,
  <https://docs.aws.amazon.com/STS/latest/APIReference/API_GetCallerIdentity.html>
- Regiones: <https://docs.aws.amazon.com/accounts/latest/reference/manage-acct-regions.html>
