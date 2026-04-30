import abc
from abc import ABC, abstractmethod


class IFoo(abc.ABC):
    @abstractmethod
    def run(self, value: int) -> str:
        pass


class IBar(ABC):
    @abstractmethod
    def stop(self) -> None:
        pass


class Foo(IFoo):
    def run(self, value: int) -> str:
        return str(value)
