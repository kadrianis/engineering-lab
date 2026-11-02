# LABS_ — Lire une tâche SaaS avec un contrat vérifié

Ce mini-projet relie le lecteur HTTP résilient à l'API .NET `modular-jobs` du dépôt. Un tableau de bord peut afficher une tâche existante ou son absence sans accepter un JSON incohérent. Aucune dépendance npm. Node 22 et SDK .NET fixé par `global.json` pour l'intégration.

## Installation et lancement

Depuis la racine du dépôt, après installation des runtimes :

```sh
node --version
dotnet --version
node --test labs/resilient-reader/reader.test.mjs labs/resilient-reader/jobs.test.mjs
node labs/resilient-reader/jobs.integration.mjs
```

La dernière commande compile l'API en Release, choisit un port loopback temporaire, lance Kestrel, crée une tâche synthétique, vérifie la lecture et le cas absent, puis arrête le processus. Aucun service cloud ni compte requis. Un accès aux sources NuGet peut être nécessaire au premier restore même sans dépendance tierce.

Pour une utilisation interactive, lancer `dotnet run --project labs/modular-jobs/Api -- --urls http://127.0.0.1:5080`. Dans un autre terminal PowerShell :

```powershell
$job = Invoke-RestMethod http://127.0.0.1:5080/jobs -Method Post -ContentType application/json -Body '{"title":"Synthetic report"}'
$env:JOB_ID = $job.id
node --input-type=module -e "import {createJobsClient} from './labs/resilient-reader/jobs.mjs'; console.log(await createJobsClient('http://127.0.0.1:5080').getJob(process.env.JOB_ID));"
```

Arrêter l'API avec Ctrl+C. Le stockage mémoire est effacé au redémarrage. Les reprises concernent seulement GET ; la création de démonstration est un POST unique.

## Cours : du transport au contrat métier

1. **Fixer la destination.** `createJobsClient(origin)` reçoit une configuration de confiance, sans chemin, credentials, query ni fragment. HTTPS est obligatoire sauf loopback/localhost en développement. L'identifiant public ne peut jamais choisir un hôte ou injecter un chemin : il doit être un GUID hexadécimal avec tirets. L'origine est capturée sous forme de chaîne immuable.
2. **Réutiliser les bornes.** `read` conserve trois tentatives maximum, Retry-After et redirections manuelles. Le client métier impose 8192 octets maximum et un budget global de 5000 ms par défaut (surcharge `timeoutMs` et signal d'annulation acceptés). Attente des reprises et lecture du corps partagent ce budget. Un timeout n'est pas une absence de tâche.
3. **Interpréter HTTP.** 404 devient `{kind:'not-found', attempts}`. Seul 200 peut produire `{kind:'found', job, attempts}`. Une redirection, 401, 403 ou 503 final produit `JobReadError('HTTP_ERROR', status)`. Aucun corps d'erreur n'est exposé dans le message d'erreur. Une erreur réseau conserve le type du transport.
4. **Vérifier le document.** JSON valide ne signifie pas contrat valide. Le champ `id` doit correspondre au GUID demandé, sans distinction de casse. `title` doit être non vide après trim et limité à 120 caractères. Le statut doit être `pending`, seul état actuellement produit par le serveur. L'objet retourné ne garde que ces trois champs. Des propriétés JSON supplémentaires sont ignorées ; un nouvel état serveur nécessite une évolution explicite du contrat.

L'identité validée est celle de la tâche. Ce contrôle ne prouve ni l'identité d'un utilisateur ni son droit d'accès. Pour un SaaS multi-tenant, l'API doit authentifier l'utilisateur et autoriser la lecture pour son tenant avant de retourner la tâche.

## Exercice et correction complète

Objectif : afficher « introuvable » pour 404, rejeter une réponse 200 dont l'ID diffère, et tolérer un 503 suivi d'un 200 valide. Commencer par écrire les trois tests avec un vrai serveur HTTP local ; implémenter ensuite le client sans ajouter de retry POST.

Solution exécutable complète : [`jobs.mjs`](jobs.mjs), tests [`jobs.test.mjs`](jobs.test.mjs), intégration [`jobs.integration.mjs`](jobs.integration.mjs). Le transport complet reste dans [`reader.mjs`](reader.mjs). Utilisation corrigée :

```js
import { createJobsClient, JobReadError } from './jobs.mjs';
const client = createJobsClient('http://127.0.0.1:5080');
try {
  const result = await client.getJob('00000000-0000-0000-0000-000000000000');
  console.log(result.kind === 'found' ? result.job.status : 'introuvable');
} catch (error) {
  if (error instanceof JobReadError) console.error(error.code, error.status);
  else throw error; // timeout, annulation, réseau ou configuration
}
```

Le test 503→200 vérifie deux requêtes GET sur le chemin exact et retire un champ interne fictif. Les tests de contrat couvrent JSON illisible, null, tableau, champs absents, ID différent, titre vide/trop long et statut inconnu. Les tests de transport couvrent corps trop grand, annulation avant I/O et corps bloqué. Le test d'intégration emploie le serveur .NET réel, et non une imitation du contrat. Il ne simule pas de changement d'état : l'API actuelle ne possède pas ce mécanisme.

## Architecture et alternatives

Un petit adaptateur au-dessus du transport sépare les reprises réseau de la décision métier. Aucun framework ni bibliothèque de schémas pour ce contrat à trois champs. Une bibliothèque de validation devient utile quand les contrats se multiplient ; un client généré depuis OpenAPI réduit la duplication mais demande un contrat publié et une chaîne de génération. Un simple `fetch().json()` est plus court, mais ne couvre pas ici le budget global, les reprises et la taille. Le polling continu n'est pas ajouté : il nécessiterait une cadence, un arrêt, une borne totale et une vraie transition serveur. SSE peut convenir ensuite pour éviter les lectures répétées.

## Vérification et limites

Validation locale du 2 novembre 2026 : Node v22.22.2, SDK .NET 10.0.402. Les 41 tests Node passent (21 transport existants + 20 métier). L'intégration réelle vérifie 201, lecture identique, puis absence (3 assertions). La CI rejoue ces tests et les contrôles .NET/Bicep sur le commit publié ; les preuves datées de cette exécution restent dans le compte rendu privé.

La version locale est celle réellement testée, pas une revendication du dernier patch disponible. La page officielle de versions confirme le maintien de la branche 22 au jour de consultation ; la documentation URL consultée est la version courante, seules les fonctionnalités disponibles sur Node 22 sont utilisées.

Ni authentification, isolation tenant, persistance, déploiement ni test de charge. L'origine doit venir d'une configuration administrée : ce filtre n'est pas une protection générale SSRF contre DNS hostile, proxy ou résolution modifiée. Une petite course existe entre la sélection du port d'intégration et le lancement Kestrel ; un conflit fait échouer le test. La validation `pending` est volontairement stricte. Les erreurs de parsing ne sont pas reprises. Le header Content-Type n'est pas validé, le corps et son contrat le sont.

Prochaine étape : ajouter authentification et autorisation tenant côté API avant toute exposition, puis prouver qu'un utilisateur ne peut pas lire une tâche d'un autre tenant.

## Sources officielles consultées le 2 novembre 2026

- [Versions et support Node.js](https://nodejs.org/en/about/previous-releases)
- [API WHATWG URL, origin et composants](https://nodejs.org/api/url.html)
- Contrat serveur inspecté dans `labs/modular-jobs/Api/ApiHost.cs` et `Jobs.cs` du dépôt ; aucune documentation externe ne remplace ce contrat réel.
