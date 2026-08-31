#!/usr/bin/env bash
set -euo pipefail

if [[ ${EUID} -ne 0 ]]; then
  echo 'Run as root.' >&2
  exit 1
fi

install_root='/opt/mmprotect-instance-agent'
state_root='/var/lib/mmprotect-agent'
config_root='/etc/mmprotect-instance-agent'
nginx_root='/etc/nginx/mmprotect.d'

if ! id -u mmprotect-agent >/dev/null 2>&1; then
  useradd --system --home-dir "${state_root}" --shell /usr/sbin/nologin --user-group mmprotect-agent
fi

install -d -m 0700 -o mmprotect-agent -g mmprotect-agent "${state_root}"
install -d -m 0750 -o root -g mmprotect-agent "${config_root}"
install -d -m 0755 -o root -g root "${nginx_root}" /var/www/letsencrypt

install -m 0644 deploy/instance-agent/mmprotect-instance-agent.service /etc/systemd/system/mmprotect-instance-agent.service
install -m 0644 deploy/instance-agent/nginx/mmprotect-include.conf /etc/nginx/conf.d/mmprotect-instance-agent.conf

if [[ ! -f "${config_root}/environment" ]]; then
  install -m 0600 -o root -g mmprotect-agent /dev/null "${config_root}/environment"
  cat >&2 <<'EOF'
Created /etc/mmprotect-instance-agent/environment.
Set InstanceAgent__ApiKey and InstanceAgent__LicenseServerImage before enabling the service.
EOF
fi

if [[ ! -f "${install_root}/MmProtect.InstanceAgent.dll" ]]; then
  cat >&2 <<EOF
Warning: published agent files were not found in ${install_root}.
Copy the published application there before starting the service.
EOF
fi

systemctl daemon-reload
echo 'Installation artifacts installed. Review the environment file, then run: systemctl enable --now mmprotect-instance-agent'
