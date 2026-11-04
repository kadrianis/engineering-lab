# LABS_ — Création de tâches sans doublons dans un processus

## Objectif SaaS et contrat

Un opérateur relance une création après avoir perdu sa réponse réseau. Avec une même clé et un même titre normalisé, le serveur renvoie la tâche initiale. Les clients sans clé conservent le comportement existant : chaque POST crée une nouvelle tâche. Le lecteur Node reste GET uniquement.

Contrat propre à cette API : en-tête facultatif `X-Job-Request-Key`, exactement un GUID au format D (36 caractères, casse indifférente). Ce nom personnalisé évite de revendiquer la conformité au projet IETF Idempotency-Key. Aucun secret dans la clé : utiliser un UUID aléatoire neuf pour chaque intention de création. Conserver la clé si la réponse est perdue.

| Situation | Statut | Effet |
| --- | --- | --- |
| Nouvelle clé et titre valide | 201 | Nouvelle tâche, corps JSON et Location |
| Même clé et titre après Trim identique | 200 | Même corps et même Location |
| Même clé et titre différent | 409 | ProblemDetails, aucune mutation |
| Clé vide, malformée ou multiple | 400 | Aucune réservation de clé |
| Titre invalide | 400 | Aucune réservation de clé |
| Capacité de 1000 tâches atteinte | 503 | Aucune admission nouvelle, rejeux conservés |
| Budget HTTP épuisé | 429 | Pas d'accès au store, règle existante |

Les réponses produites par le handler POST portent Cache-Control: no-store. Les erreurs de désérialisation produites avant ce handler suivent les réponses ASP.NET existantes.

## Installation et lancement

Prérequis : SDK .NET 10.0.402 ou patch compatible selon global.json, Node 22 pour le lecteur. Aucun package NuGet tiers ou npm. Les versions réellement installées sont à lire avec `dotnet --info` et `node --version` ; aucune mise à niveau automatique.

```powershell
dotnet run --project labs/modular-jobs/Checks -c Release
node --test labs/resilient-reader/reader.test.mjs labs/resilient-reader/jobs.test.mjs
node labs/resilient-reader/jobs.integration.mjs
node labs/resilient-reader/jobs.integration.mjs --rate-limit
dotnet run --project labs/modular-jobs/Api -- --urls http://127.0.0.1:5080
```

Dans un second PowerShell, lancer cette démonstration réelle :

```powershell
$key = [guid]::NewGuid().ToString()
$headers = @{ 'X-Job-Request-Key' = $key }
$body = @{ title = '  Export synthétique  ' } | ConvertTo-Json
$a = Invoke-WebRequest http://127.0.0.1:5080/jobs -Method Post -Headers $headers -ContentType application/json -Body $body
$b = Invoke-WebRequest http://127.0.0.1:5080/jobs -Method Post -Headers $headers -ContentType application/json -Body $body
$a.StatusCode # 201
$b.StatusCode # 200
$a.Content -eq $b.Content # True
```

Modifier le titre en conservant la clé donne 409. Ne jamais exposer ce lab à Internet : absence d'identité, d'autorisation et de persistance. Aucun déploiement requis.

## Cours : pourquoi vérifier puis insérer ne suffit pas

Deux requêtes peuvent toutes deux observer une clé absente et créer chacune un travail. ConcurrentDictionary protège une opération isolée ; il ne rend pas atomique une séquence de plusieurs opérations. Ici, une seule section critique protège recherche de clé, comparaison du titre, admission, insertion de la tâche et mémorisation de la clé. Aucun await ni I/O ne se trouve sous ce verrou.

Le titre est validé et normalisé avant le verrou. La comparaison est sensible à la casse, sur le titre après Trim ; aucun autre champ métier n'existe aujourd'hui. Ajouter un champ de création impose de l'inclure dans cette comparaison. Le titre précédent n'est jamais renvoyé dans le message de conflit.

Le rejeu est examiné avant le plafond de capacité. Il ne consomme aucune nouvelle place dans le store, mais il consomme un permis HTTP comme tout autre appel. Les erreurs ne sont pas mises en cache. Une nouvelle clé rejetée n'est pas mémorisée. Chaque clé conservée correspond à une tâche : au maximum 1000 entrées supplémentaires, sans purge ni TTL. Une clé est valable jusqu'au redémarrage du processus.

Il s'agit d'une déduplication de créations mémoire, pas d'une garantie exactly-once d'un effet externe. Une panne ou deux instances peuvent encore créer deux tâches. Aucun worker, paiement, email ou effet client n'est exécuté par ce code.

## Exercice

Ajouter un POST facultativement dédupliqué. Prouver qu'un burst de vingt requêtes avec la même clé donne exactement un 201 et dix-neuf 200, un seul identifiant et une Location commune. Prouver qu'un conflit ne remplace pas le premier résultat, qu'un échec de validation ne réserve pas de clé et qu'un rejeu fonctionne encore à capacité pleine. Garder les tests existants sur le quota.

## Correction complète

Dans Api/Jobs.cs, Creation porte Job et Replayed ; Create délègue à CreateOnce sans clé. CreateOnce normalise le titre puis prend le verrou partagé, cherche la clé, compare le titre et renvoie le résultat existant. En absence de clé connue, il vérifie le plafond, crée le Job et mémorise le couple clé/tâche. JobKeyConflictException distingue le conflit métier d'une capacité saturée.

Dans Api/ApiHost.cs, le handler valide la cardinalité et Guid.TryParseExact avant tout appel au store. Il construit 201/200 et Location selon Replayed, 409 sur conflit, 400 sur validation, 503 sur capacité. Aucun corps utilisateur ni valeur de clé n'est interpolé dans les erreurs d'idempotence.

Dans Checks/Program.cs, la correction contient tests store parallèles, plafond et rejeu, validation sans réservation, puis burst Kestrel réel, erreurs HTTP et rétrocompatibilité sans clé. Le code complet de ces trois fichiers est fourni dans le dépôt et le dossier technique ; aucune étape de code à deviner.

## Architecture et alternatives

- Verrou et dictionnaire mémoire : zéro dépendance, démonstration testable, coût borné ; une seule instance et durée de vie courte.
- PostgreSQL : clé unique (tenant authentifié, opération, clé), empreinte du contenu et insertion métier dans la même transaction ; persistance et concurrence multi-instance, au prix d'une migration et d'une politique de rétention explicite.
- Redis SET NX seul : ne garantit pas l'atomicité avec une base métier ou un effet externe. Prévoir état de traitement, reprise après crash et coordination transactionnelle.
- Déduplication côté navigateur seule : utile pour l'ergonomie, insuffisante si plusieurs onglets ou une réponse perdue.

L'étape suivante est le choix du fournisseur d'identité et des refus inter-tenant, puis la transaction durable et le périmètre de clé par tenant. La clé actuelle est globale au processus et ne constitue jamais une autorisation. Ne pas ajouter de retries POST automatiques avant ces garanties et une politique de rétention définie.

## Sources consultées le 4 novembre 2026

- https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/responses?view=aspnetcore-10.0 — réponses IResult / ProblemDetails.
- https://www.ietf.org/archive/id/draft-ietf-httpapi-idempotency-key-header-07.html — document de travail publié en octobre 2025, expiration indiquée au 18 avril 2026 ; inspiration uniquement, aucune conformité revendiquée et aucun statut RFC supposé.
- https://dotnet.microsoft.com/en-us/download/dotnet/10.0 — annonce SDK 10.0.403 et runtime 10.0.14 ; installation locale 10.0.402 / 10.0.13 inchangée, mise à jour nécessaire avant exposition.
- https://nodejs.org/en/about/previous-releases — branche 22 encore LTS ; v22.22.2 est la version locale testée, pas une affirmation du dernier patch.

## Preuves et limites

Validation locale : 90 assertions .NET et 41 tests Node réussis ; intégration standard (3 assertions) et quota réel (9 assertions) réussies. Le workflow existant exécute le fichier Checks enrichi, Node et les deux intégrations. Aucun déploiement ni nouvelle dépendance.

