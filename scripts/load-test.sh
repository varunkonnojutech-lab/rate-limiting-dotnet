#!/usr/bin/env bash
# Load-test the rate limiters with plain curl. Run the API first:
#   dotnet run --project src/RateLimitingDemo.Api   # http://localhost:5090
set -u

BASE="${BASE_URL:-http://localhost:5090}"

echo "== Fixed window: 5 per 10s (6th should be 429) =="
for i in 1 2 3 4 5 6; do
  code=$(curl -s -o /dev/null -w "%{http_code}" "$BASE/fixed")
  echo "  request $i -> $code"
done

echo
echo "== Token bucket: burst of 10, then 429 (wait 3s, one more succeeds) =="
for i in $(seq 1 11); do
  code=$(curl -s -o /dev/null -w "%{http_code}" "$BASE/token-bucket")
  echo "  request $i -> $code"
done
sleep 3
code=$(curl -s -o /dev/null -w "%{http_code}" "$BASE/token-bucket")
echo "  after 3s -> $code"

echo
echo "== Per-key: alice exhausts her bucket, bob is unaffected =="
for i in 1 2 3 4 5 6; do
  code=$(curl -s -o /dev/null -w "%{http_code}" -H "X-Api-Key: alice" "$BASE/per-key")
  echo "  alice request $i -> $code"
done
code=$(curl -s -o /dev/null -w "%{http_code}" -H "X-Api-Key: bob" "$BASE/per-key")
echo "  bob request 1 -> $code"

echo
echo "== Concurrency: 5 parallel requests, 2 in flight + 1 queued = 2 rejected =="
seq 1 5 | xargs -P5 -I{} curl -s -o /dev/null -w "%{http_code}\n" "$BASE/concurrency" | sort | uniq -c

echo
echo "Done. The 429 responses carry a Retry-After header — check one with:"
echo "  curl -si $BASE/fixed | grep -i retry-after"
