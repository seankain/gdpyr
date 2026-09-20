# gdpyr — Dedicated Server Deployment (AWS EC2)

The playtest strategy: one headless Godot dedicated server on an EC2 instance with an Elastic IP.
Clients connect directly to `<elastic-ip>:7777` over UDP. No player needs port forwarding, NAT
punch-through, or a relay — the server has a public address and clients dial out.

Every milestone from M0 onward is verified against this server, not against localhost. Latency,
jitter and packet-loss bugs do not reproduce on loopback.

---

## 1. One-time AWS setup

### Instance

| Setting | Value | Why |
|---|---|---|
| AMI | Ubuntu Server 24.04 LTS, **x86_64** | arm64 (`t4g`) is cheaper, but confirm a Linux arm64 .NET export template exists before betting on it. x86_64 removes the question. |
| Type | `t3.small` (2 vCPU / 2 GB) | Ample for 8 players + 50 units. `t3.micro` also works; see the CPU-credit note in §5. |
| Storage | 16 GB gp3 | Build is a few hundred MB. |
| Key pair | new, save the `.pem` | Used by `scripts/deploy.sh`. |
| Region | nearest your playtest group | Single biggest latency lever you control. |

### Security group

Two inbound rules. That is the whole firewall.

| Type | Protocol | Port | Source |
|---|---|---|---|
| SSH | TCP | 22 | **your IP only** |
| Custom UDP | UDP | 7777 | `0.0.0.0/0` |

ENet is UDP-only — there is no TCP game rule to add. Security groups are stateful, so no outbound
rule is needed for replies.

**If and only if you use a custom Network ACL:** NACLs are *stateless*. You need inbound UDP 7777
**and** outbound UDP 1024–65535. The default VPC NACL allows everything and needs no change. This is
the single most common "the port is open but nothing connects" cause on AWS.

### Elastic IP

Allocate one and associate it with the instance so the address survives stop/start. AWS bills public
IPv4 at roughly $0.005/hr (~$3.65/mo), whether or not the instance is running — this is unavoidable
and it is the cost of a stable address.

### Bootstrap the box

```bash
ssh -i gdpyr.pem ubuntu@<elastic-ip>

sudo adduser --system --group --home /var/lib/gdpyr gdpyr
sudo mkdir -p /opt/gdpyr
sudo chown ubuntu:ubuntu /opt/gdpyr          # deploy user writes here
sudo chown gdpyr:gdpyr  /var/lib/gdpyr       # Godot user data / logs

# Godot's Linux binary dlopens its windowing libs, but minimal images are often missing these.
sudo apt-get update
sudo apt-get install -y libfontconfig1 libfreetype6

# C# builds need the .NET runtime unless your export bundles it (§2).
sudo apt-get install -y dotnet-runtime-8.0
```

If the server binary refuses to start, `ldd /opt/gdpyr/gdpyr-server` names the missing library.

---

## 2. Export

Godot 4 needs **no separate server binary**. The `Linux Server` preset is versioned in
`export_presets.cfg`; open the project in the editor once so Godot validates and normalises it
(Project → Export). Its settings:

- Platform **Linux**, architecture **x86_64**.
- Tick **Export as dedicated server**. This adds the `dedicated_server` feature tag, forces headless
  at runtime, and lets you strip client-only resources.
- Resources tab → Export Mode **Strip Visuals** (textures and materials become placeholders that keep
  only their dimensions, so scene references still resolve server-side).
- Guard client-only code with `OS.HasFeature("dedicated_server")`.

**What a C# export actually produces** — not a single file:

```
build/server/
  gdpyr-server                              # executable (PCK embedded)
  data_gdpyr-server_linuxbsd_x86_64/        # .NET assemblies + game dll
```

Both must ship. `scripts/deploy.sh` rsyncs the whole directory, which handles this.

**.NET runtime**: unlike GDScript, a C# build depends on the .NET runtime on the server. Whether the
export bundles it depends on your export settings — verify empirically by running the binary on the
clean box. If it fails looking for `hostfxr` or a runtime, `dotnet-runtime-8.0` (installed in §1)
resolves it. The project targets `net8.0`.

```bash
./scripts/export-server.sh          # writes build/server/
```

First export on a fresh checkout or in CI needs the asset import to have run — the script does
`godot --headless --import` first for this reason.

---

## 3. Deploy

```bash
export GDPYR_HOST=ubuntu@<elastic-ip>
export GDPYR_SSH_KEY=~/.ssh/gdpyr.pem

./scripts/export-server.sh && ./scripts/deploy.sh
```

`deploy.sh` rsyncs `build/server/` to `/opt/gdpyr`, restarts the systemd unit, and fails loudly if
the service does not come back up.

Install the unit once:

```bash
scp -i $GDPYR_SSH_KEY deploy/gdpyr-server.service $GDPYR_HOST:/tmp/
ssh -i $GDPYR_SSH_KEY $GDPYR_HOST \
  'sudo mv /tmp/gdpyr-server.service /etc/systemd/system/ &&
   sudo systemctl daemon-reload &&
   sudo systemctl enable --now gdpyr-server'
```

---

## 4. Verify

On the server:

```bash
systemctl status gdpyr-server
journalctl -u gdpyr-server -f            # live logs
ss -ulnp | grep 7777                     # is it actually bound?
sudo tcpdump -i any -n udp port 7777     # do client packets arrive?
```

`tcpdump` is the decisive test. If packets arrive and the client still fails, the problem is in the
game. If they do not arrive, it is the security group or a NACL — not Godot.

On the client:

```bash
gdpyr --client <elastic-ip>:7777
```

For friends on Windows, ship a desktop shortcut with the argument already appended; do not make them
type an IP.

---

## 5. Operating notes

**Cap the framerate.** Headless Godot runs its main loop as fast as the CPU allows and will hold a
core at 100%, which drains `t3`/`t4g` CPU credits within hours and then throttles you mid-playtest.
Set `Engine.MaxFps = 60` in the server bootstrap path. Confirm with `top` that the process sits well
under 100% of one core.

**Bandwidth is not a concern at this scale.** Using the 25 KB/s per-client downstream budget from
[`NETCODE.md`](NETCODE.md) §7: 8 players ≈ 200 KB/s ≈ 0.7 GB/hr. AWS's 100 GB/mo free egress covers
roughly 140 hours of full-lobby play.

**Cost**, approximate and region-dependent: `t3.small` on-demand ≈ $15/mo running continuously, plus
≈ $3.65/mo for the public IPv4, plus a little EBS. Stop the instance between playtests and compute
drops to near zero; the IPv4 and EBS charges continue.

**`export_presets.cfg` is tracked** (M0 removed it from `.gitignore`) so the server preset is
versioned and reproducible. It holds no secrets for this project — the credential risk in that file
is Android keystore passwords, which do not apply here.

**Parallel test servers**: run a second unit on port 7778 with its own `ExecStart` and security-group
rule when you want to A/B two builds in one session.

---

## 6. When this stops being enough

- **Friends find the IP-and-shortcut flow annoying** → Steam lobbies + Steam Datagram Relay (M7).
  The transport sits behind `Scripts/Net/TransportFactory.cs`, so this is one factory method.
- **Players spread across continents** → a second instance in another region. Nothing in the design
  assumes one server.
- **You want zero-touch deploys** → the same two scripts from a GitHub Actions job; no other change.
