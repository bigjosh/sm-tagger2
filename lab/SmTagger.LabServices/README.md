# ONLY a local DNS/SMTP lab helper

This separate, dependency-free `net10.0` console project supports authorized SmarterMail integration experiments. It is excluded from the production solution and release. Run it inside the same lab Windows guest as SmarterMail: `127.0.0.2` denotes that guest's loopback interface, so a helper on the host is not reachable through the guest's loopback address.

Build and check from the repository root:

```powershell
dotnet build lab/SmTagger.LabServices/SmTagger.LabServices.csproj -c Release
dotnet run --project lab/SmTagger.LabServices/SmTagger.LabServices.csproj -c Release --no-build -- --self-test
```

The self-test uses real UDP/TCP sockets on automatically allocated loopback ports. It reports counted PASS/FAIL results and exits nonzero for any failed check or unexpected helper error. Its listener-failure check deliberately stops one SMTP listener, expects one error report, and verifies that all ports can be rebound after shutdown. Fresh `sm-tagger-lab-selftest-<UUID>` directories under the OS temporary directory are retained; the SMTP capture directory is printed for inspection. The test does not alter DNS resolver settings, mail-server settings, firewall rules, or the production datadir.

Launch the Release apphost, or pass its adjacent DLL to `dotnet`:

```powershell
./lab/SmTagger.LabServices/bin/Release/net10.0/sm-tagger-lab-services.exe --config ./lab/SmTagger.LabServices/lab.example.json
```

Options are `--config <path>` (required), `--bind <IPv4-loopback>`, `--dns-port <number>`, `--smtp-port <number>`, and `--capture-dir <path>`. Defaults are `127.0.0.2`, DNS UDP+TCP port 53, and SMTP port 25. Zero requests an ephemeral port for local tests. Configuration capture paths resolve relative to the JSON file; a command-line capture path resolves relative to the working directory. Startup prints the actual ports and absolute capture directory after all listeners open. Ctrl+C stops listeners and active sessions. An unexpected listener failure also triggers shutdown and a failing exit instead of leaving the helper partially running. Exit status is 0 for clean shutdown, 1 for recorded runtime/self-test errors, and 2 for invocation/startup errors.

Reserve those listener endpoints in the lab. In particular, SmarterMail must bind its inbound SMTP to a specific different address, such as `127.0.0.1` or the guest's interface, rather than claiming wildcard `0.0.0.0:25`, which can prevent the capture listener from binding. Configure the lab resolver and SMTP routing explicitly during integration; this utility does not change them.

Listener addresses and every configured A address must be IPv4 loopback. Configure only reserved `.test` zones, owner names, and MX targets. The sample zones and identities are synthetic. Copy the example to a separate lab configuration before entering the lab server's actual DKIM selector and public key as a TXT record, for example owner `selector._domainkey.reply.sm-lab.test` and value `v=DKIM1; k=rsa; p=<actual-public-key>`. The example deliberately contains no fake DKIM key. Reload requires stopping and restarting the helper.

DNS serves only explicit A, MX, and TXT records with TTL 60. It never recurses, forwards, or resolves an MX target itself. Unknown zones return REFUSED; missing names in a configured zone return NXDOMAIN; known names without a requested type return NODATA. Zone apexes and empty nonterminal names count as known. Additional query records are structurally checked, but EDNS capability data is ignored. UDP responses larger than 512 bytes request TCP retry; TCP permits complete responses up to 65,535 bytes. TXT data is split into character strings of at most 255 UTF-8 bytes, so a complete DKIM public key can be provided as one JSON string. Bounds are 4,096 bytes per incoming network query, 16 zones, 128 records, and 4,096 UTF-8 bytes per TXT value. No SOA, NS, wildcard, CNAME, AAAA, DNSSEC, or recursive negative-cache service is provided.

SMTP supports EHLO/HELO, MAIL, RCPT, DATA, RSET, NOOP, and QUIT. It advertises SIZE and 8BITMIME. MAIL accepts a simple ASCII angle address or `<>`; RCPT accepts only simple ASCII addresses beneath `.test`. All accepted recipients are captured regardless of whether a DNS record exists for their `.test` domain. External recipients are refused. Nothing is relayed or submitted onward: the only outgoing client connections in this code are the self-test's explicit loopback connections.

Each accepted DATA transaction produces `<UUID>.eml` containing exactly the dot-unstuffed SMTP DATA bytes, including their CRLF endings, and `<UUID>.json` containing the separate envelope, greeting, peer, timestamp, and EML filename. No Received header, message normalization, signature, or authentication result is added. A successful SMTP 250 is sent only after both files are closed. A write failure can leave partial local evidence, which is never automatically retried or deleted.

The lab harness limits each SMTP transaction to 32 MiB and 1,000 recipients, commands to 4,096 bytes, DATA physical lines to 65,536 bytes, and each connection to one minute. There are at most 32 DNS TCP and 16 SMTP sessions at once. Oversized/malformed wire data or session expiry closes that session and increments the helper error count. Capture disk use is not automatically bounded or cleaned. It has no TLS, AUTH, DSN parameter support, SMTPUTF8, quoted envelope local parts, SPF/DMARC evaluator, or DKIM signer/verifier. An independent mail-authentication verifier and real deployed admission/size tests remain pending; success here establishes only local DNS and capture behavior, not a SmarterMail deployment gate.
