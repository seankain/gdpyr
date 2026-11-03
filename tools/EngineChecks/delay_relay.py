#!/usr/bin/env python3
"""A UDP relay that delays every datagram each way: a stand-in for `tc netem`.

docs/DEPLOYMENT.md §4 shapes a link with

    tc qdisc add dev eth0 root netem delay 40ms 10ms distribution normal   # 80 ms RTT, 20 ms jitter

which needs the kernel's sch_netem, and a kernel without it cannot. This does the same
to one game's traffic in user space: clients connect to --listen, the relay forwards to
--server, and each datagram, in each direction, is held for a delay drawn from a normal
distribution (--delay-ms, --jitter-ms; never less than zero) before it goes on. Each is
drawn on its own, so datagrams are reordered as netem reorders them. --loss drops that
share of datagrams, each way.

    python3 tools/EngineChecks/delay_relay.py --listen 7793 --server 127.0.0.1:7792 \\
        --delay-ms 40 --jitter-ms 10

Used by scripts/crew-vehicle-checks.sh's `net` mode (docs/GROUND_VEHICLES.md §12, GV3).
Prints a line of counts every ten seconds and on exit.
"""

import argparse
import asyncio
import random
import signal
import statistics
import sys


class Link:
    def __init__(self, args):
        self.mean = args.delay_ms / 1000.0
        self.sd = args.jitter_ms / 1000.0
        self.loss = args.loss
        self.random = random.Random(args.seed)
        self.counts = {"up": 0, "down": 0, "dropped": 0}
        self.delays = []

    def delay(self):
        d = max(0.0, self.random.gauss(self.mean, self.sd))
        if len(self.delays) < 200_000:
            self.delays.append(d)
        return d

    def dropped(self):
        if self.loss > 0 and self.random.random() < self.loss:
            self.counts["dropped"] += 1
            return True
        return False


class Upstream(asyncio.DatagramProtocol):
    """The relay's socket towards the server for one client address."""

    def __init__(self, relay, client):
        self.relay = relay
        self.client = client
        self.transport = None
        self.waiting = []

    def connection_made(self, transport):
        self.transport = transport
        for data in self.waiting:
            self.transport.sendto(data)
        self.waiting.clear()

    def datagram_received(self, data, addr):
        link = self.relay.link
        if link.dropped():
            return
        link.counts["down"] += 1
        loop = asyncio.get_running_loop()
        loop.call_later(link.delay(), self.relay.transport.sendto, data, self.client)

    def send(self, data):
        if self.transport is None:
            self.waiting.append(data)
        else:
            self.transport.sendto(data)


class Relay(asyncio.DatagramProtocol):
    def __init__(self, link, server):
        self.link = link
        self.server = server
        self.transport = None
        self.upstreams = {}

    def connection_made(self, transport):
        self.transport = transport

    def datagram_received(self, data, addr):
        up = self.upstreams.get(addr)
        loop = asyncio.get_running_loop()
        if up is None:
            up = Upstream(self, addr)
            self.upstreams[addr] = up
            loop.create_task(
                loop.create_datagram_endpoint(lambda: up, remote_addr=self.server))
        if self.link.dropped():
            return
        self.link.counts["up"] += 1
        loop.call_later(self.link.delay(), up.send, data)


def summary(link):
    d = link.delays
    if not d:
        return "relay: nothing relayed"
    ms = [x * 1000.0 for x in d]
    return (f"relay: up {link.counts['up']} down {link.counts['down']} dropped {link.counts['dropped']};"
            f" one-way delay mean {statistics.fmean(ms):.1f} ms, sd {statistics.pstdev(ms):.1f} ms,"
            f" min {min(ms):.1f}, max {max(ms):.1f}")


async def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--listen", type=int, required=True)
    parser.add_argument("--server", required=True, help="host:port")
    parser.add_argument("--delay-ms", type=float, default=40.0)
    parser.add_argument("--jitter-ms", type=float, default=10.0)
    parser.add_argument("--loss", type=float, default=0.0)
    parser.add_argument("--seed", type=int, default=1)
    args = parser.parse_args()

    host, port = args.server.rsplit(":", 1)
    link = Link(args)
    loop = asyncio.get_running_loop()
    await loop.create_datagram_endpoint(lambda: Relay(link, (host, int(port))),
                                        local_addr=("127.0.0.1", args.listen))
    print(f"relay: 127.0.0.1:{args.listen} -> {host}:{port}, {args.delay_ms} ms ± {args.jitter_ms} ms each way,"
          f" loss {args.loss}", flush=True)

    stop = asyncio.Event()
    for sig in (signal.SIGTERM, signal.SIGINT):
        loop.add_signal_handler(sig, stop.set)

    while not stop.is_set():
        try:
            await asyncio.wait_for(stop.wait(), timeout=10.0)
        except asyncio.TimeoutError:
            print(summary(link), flush=True)

    print(summary(link), flush=True)


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        sys.exit(0)
