# LABS_ — Protéger le budget des requêtes SaaS

Le tableau de bord d'un SaaS relit le statut d'une tâche. Une rafale de lectures peut saturer le même serveur qui reçoit les créations. Ce mini-projet ajoute un budget partagé aux routes de tâches et relie le refus serveur aux reprises GET du client existant. Il ne crée aucun service externe.

## Installation et lancement

Prérequis : Git, Node.js 22 et SDK .NET 10 compatible avec `global.json` (10.0.402, roll-forward de patch autorisé). Aucun package npm ou NuGet tiers. Depuis la racine du dépôt :

```sh
dotnet run --project labs/modular-jobs/Checks -c Release
node --test labs/resilient-reader/reader.test.mjs labs/resilient-reader/jobs.test.mjs
node labs/resilient-reader/jobs.integration.mjs
node labs/resilient-reader/jobs.integration.mjs --rate-limit
dotnet run --project labs/modular-jobs/Api -- --urls http://127.0.0.1:5080 --JobsRateLimit:PermitLimit 3 --JobsRateLimit:WindowSeconds 10
```

Arrêter le serveur avec Ctrl+C. Les tests choisissent un port éphémère, lancent la DLL compilée et arrêtent leur serveur en `finally`. Le petit intervalle entre réservation et ouverture du port subsiste. Les tests nécessitent de vraies connexions loopback.

Dans un autre terminal PowerShell, lancer une rafale synthétique :

```powershell
1..5 | ForEach-Object {
  curl.exe -i http://127.0.0.1:5080/jobs/00000000-0000-0000-0000-000000000000
}
curl.exe -i http://127.0.0.1:5080/health
```

Dans une même fenêtre : trois réponses 404 (le GUID est absent), puis 429 ; `/health` reste 200. Le 404 compte comme une requête admise, même sans tâche. Le temps de saisie peut faire passer dans la fenêtre suivante. Les assertions concurrentes automatisées utilisent une fenêtre de 3600 secondes pour éviter cet effet.

## Cours : admission, attente et écriture

Un budget d'admission décide si une requête peut atteindre le traitement métier. Il se distingue de la capacité de stockage : le store conserve sa limite de 1000 tâches, indépendamment du nombre de requêtes HTTP. Une création admise peut donc encore recevoir 400 ou 503.

Le serveur possède un compteur partagé par les deux endpoints `/jobs` et `/jobs/{id:guid}`. Par défaut, il accepte 60 requêtes par fenêtre de 60 secondes et par processus. Une fenêtre fixe renouvelle le compteur à son échéance ; elle n'assure pas 60 requêtes sur toute période glissante de 60 secondes. Deux rafales de part et d'autre d'une frontière peuvent donc être admises.

Un appel excédentaire reçoit 429 avec un corps ProblemDetails, `Retry-After` exprimé en secondes entières et `Cache-Control: no-store`. L'arrondi supérieur évite de demander au client une reprise prématurée. Il n'y a pas de file d'attente côté serveur. Le traitement métier ne s'exécute pas après le rejet.

Le lecteur Node ne répète que les GET. Il peut attendre un délai serveur jusqu'à deux secondes, dans son budget total de cinq secondes et au plus trois tentatives par défaut. Un délai serveur plus long est rendu immédiatement comme erreur HTTP ; il n'est jamais raccourci pour obtenir une tentative supplémentaire. Avec la configuration de production locale par défaut (fenêtre de 60 secondes), l'interface doit donc afficher une invitation à patienter. L'intégration utilise une fenêtre de UNE seconde pour prouver une reprise réelle automatique ; elle ne prétend pas que tous les 429 sont récupérés automatiquement.

Un POST refusé n'est pas automatiquement répété. Si une réponse de création se perd après admission, une nouvelle création pourrait dupliquer la tâche. L'idempotence d'écriture reste un chantier distinct.

## Exercice et solution complète

Objectif : partager un budget de deux admissions entre lectures et créations, refuser le surplus, conserver la sonde de santé accessible et permettre au client GET de reprendre après renouvellement.

1. Configurer un limiteur nommé `jobs`, sans queue. Attacher la même politique aux deux endpoints métier ; laisser `/health` sans politique.
2. Refuser au démarrage des limites nulles, négatives, excessives ou non entières. Bornes retenues : 1 à 10000 admissions et 1 à 3600 secondes. Configuration via arguments ou variables `JobsRateLimit__PermitLimit` et `JobsRateLimit__WindowSeconds` ; configuration d'opérateur, jamais un header client.
3. Sur refus, calculer `max(1, ceil(RetryAfter.TotalSeconds))`, écrire les deux headers et exécuter `Results.Problem(statusCode: 429, ...)`.
4. Envoyer douze créations concurrentes dans une fenêtre longue. Vérifier deux 201, dix 429, les headers et le corps de chaque refus, puis GET refusé et santé acceptée.
5. Avec une fenêtre d'une seconde, créer une tâche, vider le budget, lire avec `createJobsClient` et vérifier le délai réel, la tâche rendue et le nombre borné d'essais.

La correction exécutable est intégralement dans [ApiHost.cs](Api/ApiHost.cs), [Checks/Program.cs](Checks/Program.cs) et [jobs.integration.mjs](../resilient-reader/jobs.integration.mjs). Le code du client se trouve dans [jobs.mjs](../resilient-reader/jobs.mjs) et [reader.mjs](../resilient-reader/reader.mjs). Les commandes ci-dessus exécutent la solution complète, sans pseudo-code à compléter. Le refus est placé avant les handlers ; les contraintes de titre et de capacité restent dans le store.

## Architecture et alternatives

Le middleware ASP.NET Core intégré évite un compteur maison avec verrous, timers et calcul de renouvellement. Une politique nommée unique garde une cardinalité constante : aucune partition basée sur une valeur arbitraire envoyée par un appelant. Les bornes empêchent une configuration désactivant involontairement le garde-fou.

Une limite de concurrence serait préférable pour borner les opérations simultanées longues, mais ne régule pas à elle seule le volume par minute. Une fenêtre glissante ou un token bucket peut réduire les rafales aux frontières, au prix d'une sémantique et de tests différents. Un gateway ou un compteur distribué devient pertinent avec plusieurs instances. Une limite par tenant exige d'abord une identité authentifiée et une autorisation serveur ; accepter simplement `X-Tenant-Id` ne les fournirait pas.

## Résultats et limites

Vérifications locales : Node v22.22.2, SDK 10.0.402 et runtime ASP.NET Core 10.0.13. 65 assertions .NET (16 antérieures et 49 supplémentaires), 41 tests Node, intégration standard de trois assertions et intégration sous quota de neuf assertions (trois de base et six de reprise). Ces tests sont fonctionnels, pas un benchmark ni un test de charge. La CI rejoue les suites et compile Bicep sans déploiement.

Le budget est en mémoire par instance : redémarrer le réinitialise, et deux instances ont deux budgets indépendants. Les clients partagent ce budget ; aucun engagement d'équité par client n'est fourni. Les routes non reconnues et la sonde de santé sont hors politique. Ce garde-fou ne constitue ni une défense DDoS complète ni un mécanisme d'authentification. L'API reste locale, sans persistance et sans isolation utilisateur. Ne pas l'exposer publiquement.

Sources officielles consultées le 3 novembre 2026 : [middleware ASP.NET Core 10](https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0), [versions Node](https://nodejs.org/en/about/previous-releases), [téléchargements .NET 10](https://dotnet.microsoft.com/en-us/download/dotnet/10.0). Node 22 figure dans les branches LTS. Microsoft annonce SDK 10.0.403/runtime 10.0.14 ; l'environnement testé ici utilise les versions locales ci-dessus. Prévoir la mise à jour de sécurité avant exposition. Aucun logiciel système n'a été modifié.

Prochaine étape : choisir le fournisseur d'identité et appliquer authentification/autorisation côté API, puis prouver le refus inter-tenant avant tout quota commercial par organisation. Pour retirer uniquement ce garde-fou, revenir au commit précédent ; aucune migration de données n'est nécessaire.
