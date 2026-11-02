# Engineering Lab

## LABS_ — Lectures SaaS résilientes

[LABS_ — Client de tâches avec contrat vérifié](labs/resilient-reader/LABS_JOBS.md) : origine configurée, GUID validé, identité de tâche et schéma contrôlés, 404 explicite, 20 tests métier et intégration réelle avec l'API .NET. Installation, cours, exercice et correction complète inclus.

[Client HTTP GET avec reprises bornées](labs/resilient-reader/README.md) : Node.js 22 sans dépendance npm, Retry-After, budget temporel global, limite de réponse et 21 tests. Cours, exercice corrigé et démonstration locale inclus. Ce lecteur complète le suivi de tâches SaaS ; il ne répète aucune écriture.

```sh
node --test labs/resilient-reader/reader.test.mjs
node labs/resilient-reader/demo.mjs
```

API de travaux techniques synthétiques : monolithe modulaire .NET 10, contrat HTTP, validation métier et stockage mémoire borné. Lab Bicep : compte Storage Azure fermé par défaut, sans déploiement automatique.

## Lancement

SDK .NET 10.0.402, aucun package NuGet tiers.

```powershell
dotnet run --project labs/modular-jobs/Checks -c Release
dotnet run --project labs/modular-jobs/Api -- --urls http://127.0.0.1:5080
```

Le [cours complet corrigé](docs/COURS.md) contient exercice, commandes de démonstration, contrat, schéma de séquence, tests, alternatives DB, sécurité, rollback et questions corrigées. Lire aussi [ADR 001](docs/adr/001-modules.md), le [guide Azure manuel](docs/AZURE.md) et les [preuves locales](docs/VERIFICATION.md).

## État réel et limites

Création et lecture fonctionnent sur loopback ; 16 assertions réussies. Bicep compilé localement. Aucune authentification, persistance, liste, suppression ou isolation utilisateur. Redémarrer efface toutes les tâches. Ne pas exposer ce prototype à Internet. Runtime local 10.0.13 : mettre à jour avant exposition, la source officielle annonce 10.0.14. Le stockage Azure est indépendant de l'API ; aucune sauvegarde, restauration, DB, image Docker ou ressource Kubernetes n'est revendiquée.

La CI compile et vérifie, sans déployer. Les actions utilisent des tags majeurs ; l'épinglage à des SHA vérifiés reste à réaliser. Données synthétiques uniquement, aucun secret ni donnée client. Aucune licence choisie.
