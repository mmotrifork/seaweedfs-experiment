# SeaweedFS in Kubernetes
## Introduction
According to the documentation:
> SeaweedFS is a simple and highly scalable distributed file system. There are two objectives:  
> 1. to store billions of files!  
> 1. to serve the files fast!

SeaweedFS (Seaweed) can be hosted inside a Kubernetes cluster, this is possible using either a Helm chart or using the [seaweedfs-operator](https://github.com/seaweedfs/seaweedfs-operator).
A standard Seaweed configuration provides the users access to store files via S3 interface or through POSIX FUSE or REST API. But if the [seaweedfs-csi-driver](https://github.com/seaweedfs/seaweedfs-csi-driver) is used RWX PVCs can be provisioned on top of Seaweed as well.

## Architecture
A Seaweed cluster consists of these [Components](https://github.com/seaweedfs/seaweedfs/wiki/Components):
### Master
This is the orchestrator
### Volume Server
This is where data is actually stored. Seaweed stores data in volumes which is a blob of data. A volume can contain multiple files and a file can span multiple volumes.
### Filer
This is what manage files in the Volume Server. It stores metadata that describe where to find the data related to a file. It is possible to choose different Filer Stores including 
### S3 Server
This exposes an S3 API that can be secured using Identity Access Management (IAM). By using the S3 API buckets can be provisioned (in volumes on the Volume Server, managed by the Filer). See documentation on [IAM Support](https://github.com/seaweedfs/seaweedfs-operator/blob/master/IAM_SUPPORT.md).
### Admin Server
This hosts a web UI in which the Seaweed cluster can be managed and or monitored. It is also where automated maintenence tasks are scheduled.
> Note: the Admin server is still work in progress. See [Admin UI documentation](https://github.com/seaweedfs/seaweedfs/wiki/Admin-UI)
### Worker Service
This is a worker that communicates with the Admin Server to perform maintenance tasks such as volume balancing, volume vacuuming etc.
### CSI Driver
This consists of a Controller, and two daemonsets; Mount Servers and Node Servers. The CSI Driver is what allows Seaweed to be used as RWX PVs. See documentation on [CSI Support](https://github.com/seaweedfs/seaweedfs-operator/blob/master/CSI_SUPPORT.md).

## High Availability (HA)
It is possible to achieve HA by scaling each of the components in the cluster.  
- The **masters** does leader election, 3 instances is the minimum for HA.  
- The **filer** does not do any leader election, instead the standard behaviour is that they broadcast their meta data changes to the other filers. In tern the will be in sync. 2 instances is enough for HA, if instant failover is needed a shared filer store eg. Redis or Postgresql is needed. This way the filers do not keep state and failover from one to another is instant. See documentation on [Filer Store Replication](https://github.com/seaweedfs/seaweedfs/wiki/Filer-Store-Replication).  
- The **S3** server is stateless, 2 instances is needed for HA.  
- **Volume** server needs scaling based on replication demands. If a single copy is needed, such that data exists in 2 places, at least 3 servers are needed. This is due to the fact that if a server goes down at least 2 must exist or the volumes all turn to read only.  
- Unfortunately the **admin** server does not currently support HA. This is not critical, but it is annoying.  
- However **worker** servers do support HA, they are stateless, so even a single instance will suffice in many cases, but if the admin server is down the worker will not perform any tasks.  

The SeaweedFS documentation has a section called [Production Setup](https://github.com/seaweedfs/seaweedfs/wiki/Production-Setup) which covers much of this in details.

## Filer Store
This cluster uses **PostgreSQL** as the filer store, replacing the default on-disk `leveldb2`.
Both filer replicas share a single `filemeta` table, so they hold no local metadata state and failover between them is instant (see [Filer Store Replication](https://github.com/seaweedfs/seaweedfs/wiki/Filer-Store-Replication)).

### The database
Postgres is run by the [CloudNativePG](https://cloudnative-pg.io/) operator (`clusters/seaweedfs-cluster/cnpg`). The instance itself lives in `clusters/seaweedfs-cluster/postgresql`:
- Single instance, no HA, no backup — this is a test cluster.
- Database `seaweedfs`, owned by role `seaweedfs`.
- **Storage class must not be `seaweedfs`.** That class is backed by the SeaweedFS CSI driver, which mounts through the filer — and the filer now stores its metadata in this database. Pointing Postgres at it creates a dependency cycle that can never resolve on a cold start. It uses kind's `standard` (local-path) class instead.
- No `postgresql.synchronous` block: with a single instance there is no standby to acknowledge writes, so requiring a synchronous replica would block every commit indefinitely.

### The `filemeta` table
The `[postgres]` filer store does **not** create its own table (`CreateTableSqlTemplate` is empty in `weed/filer/postgres/postgres_store.go`), unlike `[postgres2]`:
```sql
CREATE TABLE IF NOT EXISTS filemeta (
  dirhash   BIGINT,
  name      VARCHAR(65535),
  directory VARCHAR(65535),
  meta      bytea,
  PRIMARY KEY (dirhash, name)
);
```
This is applied by a `wait-for-filer-store` **initContainer** on the filer, not by CNPG's `bootstrap.initdb.postInitApplicationSQL`. The initContainer also blocks until `pg_isready` succeeds, so the filer never starts against a store it cannot reach.

The reason for not using `postInitApplicationSQL` is that it runs **only on first bootstrap**, and CNPG's webhook rejects most later edits to `bootstrap`. Recreating the Cluster over a surviving PVC would silently skip it, and the filer would fail with `relation "filemeta" does not exist`. `CREATE TABLE IF NOT EXISTS` in an initContainer converges on every filer rollout instead.

> The primary key is a btree over `(dirhash, name)` where `name` is `VARCHAR(65535)`. PostgreSQL's btree row limit is ~2704 bytes, so pathologically long file names will fail to index. This is SeaweedFS's own documented schema.

### Credentials
CNPG generates the `postgres-primary-app` secret; no password is committed to git.

Two things bridge the namespace gap:
1. `secretKeyRef` cannot cross namespaces, so a Kyverno `ClusterPolicy` (`postgresql/credentials-clone-policy.yaml`) clones `postgres-primary-app` from `postgresql` into `seaweedfs` with `synchronize: true`. This needs an aggregated ClusterRole, since Kyverno has no access to Secrets by default.
2. The filer reads the password from the environment rather than from `filer.toml`. SeaweedFS loads its config through viper with `AutomaticEnv` and env prefix `weed` (`weed/util/config.go`), so `WEED_POSTGRES_PASSWORD` supplies — and takes precedence over — `postgres.password`.

### TLS
CNPG serves TLS using its own internal CA. The filer connects with `sslmode = "require"`, which encrypts the connection without verifying the chain. For `verify-full`, mount the `postgres-primary-ca` secret into the filer and set `sslrootcert`.

### Ordering
Flux dependencies: `postgresql` dependsOn `cnpg` (for the CRDs) and `kyverno` (for the ClusterPolicy CRD); `seaweedfs` dependsOn `seaweedfs-operator` and `postgresql`.

`wait: true` on its own does **not** wait for PostgreSQL — Flux's kstatus has no rule for `postgresql.cnpg.io/Cluster` and ignores CNPG's `Ready` condition, so the object counts as healthy the moment it is admitted. `postgresql-sync.yaml` therefore adds a `healthCheckExprs` CEL check on that condition.

The credential clone is **not** ordered, and cannot be: the `seaweedfs` namespace is created by the same Flux apply that creates the `Seaweed` CR, so Kyverno clones the secret on that namespace's CREATE event. The cloned Secret is also not in any Flux inventory, so nothing can wait on it. In practice the filer pods sit in `CreateContainerConfigError` for a few seconds and kubelet retries — it self-heals, but expect noise on a cold bootstrap.

### Useful commands
```bash
# Cluster health
kubectl -n postgresql get cluster postgres-primary

# Inspect the metadata table
kubectl -n postgresql exec -it postgres-primary-1 -- \
  psql -U postgres -d seaweedfs -c 'SELECT count(*) FROM filemeta;'

# Confirm the filer picked up the postgres store
kubectl -n seaweedfs logs sts/seaweed-sample-filer | grep -i postgres

# Schema/connectivity gate
kubectl -n seaweedfs logs sts/seaweed-sample-filer -c wait-for-filer-store

# Verify the cloned credential landed
kubectl -n seaweedfs get secret postgres-primary-app
```

### Cutover from leveldb
The switch starts with an **empty** store — the metadata previously in `leveldb2` is not migrated. Existing volume data is still on the volume servers but is unreferenced, so S3 buckets and CSI-provisioned PVs will appear empty. To get back to a clean state, delete and re-create the S3 bucket / PVC resources (`test-app`, `test-app-intruder`) after the filers come up, and vacuum the orphaned volumes from the admin UI.

If you ever do want to preserve metadata, dump it before cutover with `weed filer.meta.backup` (or copy the tree with `weed filer.copy`) and replay it against the new store.

## Data safety
There are 2 mechanisms to ensure data safety in SeaweedFS. The primary one is **replication** the secondary is **erasure coding (EC)**.
### Replication
Replication (see [Replication](https://github.com/seaweedfs/seaweedfs/wiki/Replication)) is configured using a 3 digit system, **XYZ** eg. 001, each digit represents a topology level and the value is the number of copies on the given level. **X** is Data Center, **Y** is Rack, and **Z** is Server.  
Here is a couple of examples:
- 000: No replication, only the original data exists
- 001: One copy on a different server in the same Rack. 2 copies in total.
- 010: One copy in a different rack in the same data center. 2 copies in total.
- 123: One copy in a different data center, 2 copies in different racks in the same data center, 3 copies in the same rack. 7 copies in total.  
The cost of the replication is linear, so 001 will take up double the storage, 111 will be 4X the storage.  
> Note that it is recommended to have nodes tag according to the topology see [Topology Support](https://github.com/seaweedfs/seaweedfs-operator/blob/master/TOPOLOGY_SUPPORT.md)
### Erasure Coding (EC)
To reduce storage overhead SeaweedFS supports EC on warm and cold storage (See [Erasure Coding for warm storage](https://github.com/seaweedfs/seaweedfs/wiki/Erasure-Coding-for-warm-storage)). The OSS version of SeaweedFS supports a single configuration (10+4). In this configuration the volume marked for EC will be split into 10 data shards and 4 parity shards will be created from the data shards. With this configuration data can recorver up to 4 lost shards. The fact that at most 4 shards can be lost at a time sets a requirement of at least 4 volume servers.  
Using 10+4 EC reduce the data overhead from the total number of replicas to 1.4.  
Enterprise lincense holders get more configuration options eg. 20+4 which gives an overhead of 1.2. Lisense holders also gain advanced features such at automatic bitrot scrubbing and correction.  
Most of the automatic features available in the enterprise edition is possible to perform in the OSS see for example the documentation on [EC Bitrot Detection](https://github.com/seaweedfs/seaweedfs/wiki/EC-Bitrot-Detection).
## Security
SeaweedFS has support for robust security in many places. See the official documentation [Security Overview](https://github.com/seaweedfs/seaweedfs/wiki/Security-Overview).
### Pod Security Standards
SeaweedFS is able to be tied down with these configurations:
```yaml
podSecurityContext:
  runAsNonRoot: true
  runAsUser: 1000
  seccompProfile:
    type: RuntimeDefault
containerSecurityContext:
  allowPrivilegeEscalation: false
    capabilities:
      drop:
      - ALL
```
The only exception to this is the CSI Driver which needs priviledge escalation and SYS_ADMIN capabilities.
> Note: the volume server also needs priviledges if it relies on local path mount

### Encryption
It is possible to enable encryption of the data at rest. There are a couple of options:
1. Encrypt data on Volume Servers, this is managed by the Filer server. Seperate keys are used for each file. See [Filer Data Encryption](https://raw.githubusercontent.com/wiki/seaweedfs/seaweedfs/Filer-Data-Encryption.md).
1. The S3 API supports the same Server-Side Encryption (SSE) options as Amazon S3 (see [SSE](https://github.com/seaweedfs/seaweedfs/wiki/Server-Side-Encryption))

In-transit data can also be encrypted, for gRPC calls this is done using mTLS and for HTTP endpoints it is done using HTTPS, see [Security Overview](https://github.com/seaweedfs/seaweedfs/wiki/Security-Overview).

### Authentication
**AdminUI** can be secured by a password in the OSS variant, in enterprise OIDC is possible, see [Admin UI documentation](https://github.com/seaweedfs/seaweedfs/wiki/Admin-UI).

The **S3** API use IAM (Identity Access Management) to handle authentication. Using the seaweedfs-operator it is possible to create IAM identities, policies, policybindings and credentials, see the [readme](https://github.com/seaweedfs/seaweedfs-operator/tree/master#declarative-iam-identities-credentials-policies).

**S3** can also be secured by OICD, see [S3 OICD Integration](https://github.com/seaweedfs/seaweedfs/wiki/OIDC-Integration).

Both the **Filer** and the **Volume Server** support using JWTs for access control. See documentation on [Security Overview](https://github.com/seaweedfs/seaweedfs/wiki/Security-Overview#securing-volume-servers) for more information.

### Resource Reference Grants
By default it is not possible to reference a SeaweedFS instance outside the namespace of the instance. Referencing is needed when using CRDs such as S3 bucket, S3 Identities etc.  
To allow certain namespaces access to a SeaweedFS instance a ResourceReferenceGrant has to be created.  
A ResourceReferenceGrant lives in the namespace of the SeaweedFS instance and contains a white list of CRDs (kinds) in specific name spaces that are allowed to reference the SeaweedFS instance.  
This can be used to limit who is able to use SeaweedFS.

### Network Policies
In this SeaweedFS instance all networking has been restricted using CiliumNetworkPolicies.  
There is default deny, and all required openings have been made.  
If a new app is to use parts of SeaweedFS (except PVs through the CSI Driver) openings have to be made eg. to the S3 server.

### RBAC
SeaweedFS comes with RBAC, the developers does not grant SeaweedFS access to everything, although the nature of the operator does require somewhat broad access.

## Multi-tenancy
SeaweedFS OSS does not support multi-tenancy out of the box. With an enterprise license it is possible to toggle multi-tenancy on.  
The consequence of this is that if a tenant is given direct Filer access they can access files from other tenants. Given the use cases we forsee there is no need for tenants to have Filer access.  
PV and PVC are as secure as for other systems.  
S3 access is controlled through IAM, there is a catch here. If tenants are allowed to create the CRDs to provision S3 buckets, S3 identities, S3 policies, and S3 policybindings, it is possible to create policies that grant access to other tenants S3 buckets. To avoid this Kyverno policies have been made to restrict S3 buckets naming to match name space and S3 policies to target buckets whose name match the namespace, this way it is not possible to access buckets in other tenants namespaces.

## Volume Management
In SeaweedFS data is stored in chunks/blobs of data called a volume. When replicating data it is done on volume level.  
When designing your storage setup there are some considerations regarding volumes that must be made. The size of the volumes being the main one.  
Choosing the volume size is a trade off between a few different factors.  
Let's compare 2 configurations.

| Size | 1 GB | 30 GB |
| --- | --- | --- |
| Memory consumption (master server) | Higher | Lower |
| Free space needed on volume server for compaction | Lower | Higher |
| Time to perform compaction | Fast | Slow |
| Time to restore from replication | Fast | Slow |
| Max number of volumes | More | Fewer |

To make the trade-off you need to take the workload and usage patterns into consideration.  
Each different;
- Replication variant
- S3 Bucket
- Collection
- PV

results in volume creation. If you run out of volumes the above cannot be created.  
For a system with many buckets, PVs etc. and no need for large amounts of data a lower volume size is better. If each bucket, PV etc. is going to contain many GBs of data, then a higher volume size is better.
Read more about volume management in the documentation [Volume Management](https://github.com/seaweedfs/seaweedfs/wiki/Volume-Management).

### Configuration
The primary settings to consider are:  
- `max=0` on the Volume server instructs SeaweedFS to make volumes until the server is full
- `volumeSizeLimitMB=1024` on the Master server to set the global max volume size to 1 GB

### Data recovery
To be written

### Maintenance
To be written

## Observability
[System Metrics](https://github.com/seaweedfs/seaweedfs/wiki/System-Metrics)
To be written