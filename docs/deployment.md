# Deployment

The website and SQL Server run in Docker Compose on the application server.
Caddy runs on a separate server, handles public HTTPS, and forwards all
requests to a single HTTPS port on the application server.

| Traffic | Public endpoint | Application endpoint |
| --- | --- | --- |
| Website and WebSocket | HTTPS :443 | HTTPS :5000, HTTP/1.1 or HTTP/2 |
| Runner RPC | HTTPS :443 | HTTPS :5000, HTTP/2 |

Kestrel accepts both HTTP/1.1 and HTTP/2 over TLS. Its protocol and certificate
settings are in [appsettings.Production.json](../Olymp.Site/appsettings.Production.json).
Compose selects HTTPS port 5000 in production and HTTP port 5000 in development.
The production health check uses HTTPS and verifies the application certificate.

## Application

Create `.env` next to [compose.yaml](../compose.yaml) and
[compose.prod.yaml](../compose.prod.yaml):

```dotenv
DB_PASSWORD=replace-with-the-database-password
ApiKeys__0=replace-with-the-runner-key
ReverseProxy__KnownProxies__0=192.0.2.10
MailService__UserName=replace-with-the-smtp-username
MailService__Password=replace-with-the-smtp-password
```

Replace the example values and keep `.env` out of Git. The proxy address is the
immediate peer seen by the application; Docker NAT may change it. If needed,
enable Debug logging for
`Microsoft.AspNetCore.HttpOverrides.ForwardedHeadersMiddleware` to identify the
unknown proxy address.

On the Linux/WSL application host, run the VS Code task
**Host: Prepare production**, or run from the repository root:

```bash
bash scripts/prepare-production.sh 192.0.2.20
```

Replace `192.0.2.20` with the application server IP that Caddy connects to.
Use the same IP in Caddy's upstream address below.
The script prepares `./data` and `./backup` for SQL Server and `./keys` for the
application. It requires OpenSSL 3 or newer and creates a self-signed certificate with
subject `CN=OlympSystem`, SAN entries `IP:<server-ip>` and `DNS:localhost`, and a
lifetime of 3650 days (approximately ten years). The files are
`./tls/Olymp.WebApp.pfx` for Kestrel and `./tls/Olymp.WebApp.crt` for Caddy and the
health check. The PFX contains the private key and has no password.
Keep it private; only the CRT needs to be copied to the Caddy server.
The script sets the PFX owner to UID 1654 (`app` in the official .NET Linux
images) and its group to the host user's group, with mode `640`. The CRT mode is
`644`. The TLS directory belongs to the host user and group, with mode `751`:
the host user can list and manage certificates, and the application can access
the mounted files. The keys directory and its contents belong
to UID 1654 and the host user's group, with directory mode `2750` and existing
file mode `640`. The host user can list the directory and read existing keys;
the application can write keys. Newly generated Data Protection files may have
private permissions set by .NET; use `sudo` to read them or rerun preparation
to apply group read permissions. When launched through `sudo`, preparation uses
the original user's UID and group. It uses `sudo` for ownership and permissions when run
as a non-root user.

Development and production use the same filenames in their respective checkouts.
When updating a deployment that used older certificate filenames, rename the
existing PFX and CRT to the filenames above before preparation and update the
Caddy certificate path. This preserves the existing certificate.
Repeated preparation preserves a valid existing certificate and key contents,
and reapplies permissions. An incomplete or invalid certificate requires explicit
replacement. To replace the certificate deliberately, use
`bash scripts/prepare-production.sh <server-ip> --force`.

After preparation, build and start the application. It mounts `./tls` read-only.

```bash
docker compose -f compose.yaml -f compose.prod.yaml build site
docker compose -f compose.yaml -f compose.prod.yaml run --rm --entrypoint /app/efbundle site
docker compose -f compose.yaml -f compose.prod.yaml up -d site
```

The migration command starts SQL Server and waits for its health check.
Keep `./data`, `./backup`, `./keys`, and `./tls` persistent, and include the keys and
certificate in protected backups. The Data Protection keys protect sign-in cookies
and account tokens. Before updating, back up the database:

```bash
bash scripts/database-backup.sh -f compose.yaml -f compose.prod.yaml
```

## Caddy

Choose a persistent working directory for Caddy. Create `./certs`, `./data`, and
`./logs` there, and allow the Caddy service account to write to `./data` and
`./logs`. Copy the application server's `./tls/Olymp.WebApp.crt` to
`./certs/Olymp.WebApp.crt` and allow that account to read it.
Save this configuration as `Caddyfile` in the same working directory.
Adjust the domain names and replace `192.0.2.20` with the same application
server IP used when creating the certificate:

```caddyfile
{
	grace_period 15s
	storage file_system ./data

	servers {
		timeouts {
			read_body_idle -1s
		}
	}

	log default {
		level WARN
		output file ./logs/caddy.log {
			roll_size 5MiB
			roll_keep 3
		}
	}
}

olymp.nstu.ru {
	reverse_proxy https://192.0.2.20:5000 {
		header_up Host {hostport}
		transport http {
			tls_trust_pool file ./certs/Olymp.WebApp.crt
		}
	}
}

www.olymp.nstu.ru {
	redir https://olymp.nstu.ru{uri} permanent
}
```

The configuration stores Caddy's certificates and other state in `./data`.
It writes runtime logs at WARN level and above to `./logs/caddy.log`, rotates
them at 5 MiB, and retains three rolled files. These are runtime logs;
HTTP access logging is not enabled. `read_body_idle -1s` disables the request
body idle timeout, and `grace_period 15s` allows up to 15 seconds for active
requests to finish during shutdown. The upstream Host header preserves the
public hostname and port.

Ideally, the request body idle timeout should be disabled only for long-lived
Runner gRPC streams, which may legitimately send no messages for extended periods,
while ordinary HTTP requests retain an idle timeout. The configuration above
disables it server-wide as a compatibility workaround. Before Caddy 2.11.8,
per-route timeouts could not relax the server-wide idle timeout. Starting with
2.11.8, the experimental [`timeouts` directive](https://caddyserver.com/docs/caddyfile/directives/timeouts)
takes precedence for matching requests, so a positive `read_timeout` can extend
Runner's idle timeout without changing other routes. However, the
[2.11.8 implementation](https://github.com/caddyserver/caddy/blob/v2.11.8/modules/caddyhttp/timeouts/timeouts.go)
ignores zero and negative per-route values: `read_timeout -1s` does not disable
the inherited timeout. A large positive timeout only postpones disconnection
during prolonged inactivity; this example keeps the server-wide workaround to
allow indefinitely idle Runner streams.

Validate and start Caddy from that working directory:

```bash
caddy validate --config ./Caddyfile --adapter caddyfile
caddy run --config ./Caddyfile --adapter caddyfile
```

Caddy uses public ports 80/443 and manages public HTTPS certificates in `./data`.
Configure automatic startup with the same working directory and service account,
and preserve `./data` across restarts and upgrades. Relative storage, log, and
trust certificate paths are resolved from that working directory.
The internal certificate is managed separately by the script. Caddy verifies the
application server IP against its SAN, so no `tls_server_name` override is needed.
Restrict incoming TCP port 5000 on the application server to the Caddy server.

Before the internal certificate expires, or if the application server IP changes,
replace it using `--force`, copy the new CRT to
the Caddy server, and restart the application and reload Caddy. The script does not renew
certificates automatically.

## Runner and checks

Set Runner's `RunnerServerAddress` to `https://olymp.nstu.ru` and `ApiKey` to the
same value as the website's `ApiKeys__0`. Use the main hostname directly.

`/healthz` checks the website and database. Verify sign-in, staff pages,
and a Runner judging a submission. The application accepts forwarded IP and HTTPS
information only from configured trusted proxies.
