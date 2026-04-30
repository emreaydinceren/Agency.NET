from abc import ABC, abstractmethod
from typing import Protocol
import abc

class IFoo(ABC):
    @abstractmethod
    def run(self, value: int) -> str:
        pass

class IBar(abc.ABC):
    @abstractmethod
    def stop(self) -> None:
        pass

class IProto(Protocol):
    def execute(self, item: str) -> int:
        ...

class Foo(IFoo):
    def run(self, value: int) -> str:
        return str(value)
