from typing import Protocol
from typing_extensions import Protocol as ExtensionsProtocol


class IFoo(Protocol):
    def run(self, value: int) -> str:
        ...


class IBar(ExtensionsProtocol):
    def stop(self) -> None:
        ...
