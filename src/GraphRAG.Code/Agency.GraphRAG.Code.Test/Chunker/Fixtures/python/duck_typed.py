from typing import Protocol


class IFoo(Protocol):
    def run(self, value: int) -> str:
        ...


class Foo:
    def run(self, value: int) -> str:
        return str(value)
