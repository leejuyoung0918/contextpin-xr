"""Create deterministic Korean PCM input for the continuous timeline simulator smoke (macOS)."""
import argparse
from pathlib import Path
import subprocess
import tempfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("output", type=Path)
    output = parser.parse_args().output
    output.mkdir(exist_ok=True, parents=True)
    phrases = {
        "intro": ["전체 크기는 괜찮은 것 같아요."],
        "angle": ["등받이 각도를 조금 줄여 주세요."],
        "generation": ["완성될 때까지 잠시 기다려 봅시다."],
        "review": ["두 번째 안이 더 좋아 보여요."],
        "lip": ["이 부분은 앞쪽 받침턱입니다.", "앞쪽 받침턱의 높이를 조금 늘려 주세요."],
        "between": ["책상 공간은 충분해 보여요."],
        "closing": ["오늘 회의는 여기까지 마칠게요."],
    }
    with tempfile.TemporaryDirectory() as temporary:
        src = Path(temporary) / "line.aiff"
        for key, lines in phrases.items():
            chunks = []
            for line in lines:
                subprocess.run(["say", "-v", "Yuna", "-r", "155", "-o", str(src), line], check=True)
                pcm = subprocess.check_output(["ffmpeg", "-v", "error", "-i", str(src), "-ar", "16000",
                                               "-ac", "1", "-f", "s16le", "-"])
                chunks.append(pcm)
            # 1.3 seconds between two spoken sentences forces two STT segments in one window.
            (output / (key + ".pcm")).write_bytes(bytes(2 * 20800).join(chunks))


if __name__ == "__main__":
    main()
