#!/bin/bash

#Kind cluster
kind create cluster --config=kind-config.yaml

# Increase k8s node 'open file limits'
docker ps --format "{{.Names}}" | xargs -I {} docker exec -t {} bash -c "echo 'fs.inotify.max_user_watches=1048576' >> /etc/sysctl.conf"
docker ps --format "{{.Names}}" | xargs -I {} docker exec -t {} bash -c "echo 'fs.inotify.max_user_instances=512' >> /etc/sysctl.conf"
docker ps --format "{{.Names}}" | xargs -I {} docker exec -i {} bash -c "sysctl -p /etc/sysctl.conf"

#Cilium
cilium install --version 1.19.4
cilium status --wait

#Flux
flux bootstrap github --token-auth --owner=mmotrifork --repository=seaweedfs-experiment --branch=main --path=clusters/seaweedfs-cluster --personal
