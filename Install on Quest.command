#!/bin/zsh
cd "${0:A:h}"
python3 scripts/relay.py deploy
read -r '?종료하려면 Enter를 누르세요.'
